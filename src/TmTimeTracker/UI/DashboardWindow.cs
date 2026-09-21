using System.Net;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI;

public sealed class DashboardWindow : Form
{
    // Date plus minutes: enough to tell yesterday's clock from today's, without the seconds that
    // change every poll and make the column twitch.
    private const string RowTimeFormat = "dd MMM HH:mm";

    private readonly IEventBus _bus;
    private readonly TicketTimeRepository _tickets;
    private readonly OAuthStateRepository _oauthState;
    private readonly RememberEntryRepository _entries;
    private readonly ConfigRepository _config;
    private readonly IClock _clock;
    private readonly IClaudeCodeActivityProbe _claudeProbe;
    private readonly IClaudeSessionProbe _sessionProbe;
    private readonly IProcessLiveness _liveness;
    private readonly TrackedRepoRepository _repos;
    private readonly JiraApiClient _api;
    private readonly ILogger<DashboardWindow> _log;

    private readonly StatusPill _statePill;
    private readonly StatusPill _branchPill;
    private readonly StatusPill _ticketPill;
    private readonly StatusPill _claudePill;
    private readonly DataGridView _pending;
    private readonly Label _emptyPending;
    private readonly ListView _events;
    private readonly ComboBox _filter;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _authStatus, _lastPoll, _nextPoll;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly LinkedList<DomainEvent> _eventBuffer = new();
    private CancellationTokenSource? _subscriptionCts;

    /// <summary>
    /// Raised when the user clicks Settings. An event rather than a direct call because
    /// WindowsHost owns window lifetimes and constructs this form - calling it from here would
    /// make the dependency circular.
    /// </summary>
    public event Action? SettingsRequested;

    public DashboardWindow(
        IEventBus bus,
        TicketTimeRepository tickets,
        OAuthStateRepository oauthState,
        RememberEntryRepository entries,
        ConfigRepository config,
        IClock clock,
        IClaudeCodeActivityProbe claudeProbe,
        IClaudeSessionProbe sessionProbe,
        IProcessLiveness liveness,
        TrackedRepoRepository repos,
        JiraApiClient api,
        ILogger<DashboardWindow> log)
    {
        _bus = bus;
        _tickets = tickets;
        _oauthState = oauthState;
        _entries = entries;
        _config = config;
        _clock = clock;
        _claudeProbe = claudeProbe;
        _sessionProbe = sessionProbe;
        _liveness = liveness;
        _repos = repos;
        _api = api;
        _log = log;

        Text = "TmTimeTracker — Dashboard";
        Icon = AppIcon.Load();
        ClientSize = new Size(880, 640);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        _statusBar = new StatusStrip { Dock = DockStyle.Bottom };
        _authStatus = new ToolStripStatusLabel("Auth: —");
        _lastPoll = new ToolStripStatusLabel("Last Jira poll: —");
        _nextPoll = new ToolStripStatusLabel("Next: —");
        _statusBar.Items.AddRange(new ToolStripItem[]
        {
            _authStatus, new ToolStripSeparator(),
            _lastPoll,   new ToolStripSeparator(),
            _nextPoll
        });
        Controls.Add(_statusBar);

        _statePill  = MakePill("Idle",        PillTone.Idle,    dot: true);
        _branchPill = MakePill("no branch",   PillTone.Neutral, dot: false, mono: true);
        _ticketPill = MakePill("no ticket",   PillTone.Neutral, dot: false, mono: true);
        _claudePill = MakePill("Claude idle", PillTone.Idle,    dot: true);

        var headerFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 12, 12, 8)
        };
        foreach (var pill in new[] { _statePill, _branchPill, _ticketPill, _claudePill })
        {
            pill.Margin = new Padding(0, 0, 8, 0);
            headerFlow.Controls.Add(pill);
        }

        var pendingHeading = MakeHeading("Pending worklogs");

        _pending = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            ShowCellToolTips = false,
            ScrollBars = ScrollBars.Vertical
        };
        _pending.Columns.Add("ticket",      "Ticket");
        _pending.Columns.Add("minutes",     "Min");
        _pending.Columns.Add("first_seen",  "Started");
        _pending.Columns.Add("last_polled", "Last poll");
        _pending.Columns["ticket"].DefaultCellStyle.Font = Theme.Mono;
        _pending.Columns["minutes"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _pending.Columns["minutes"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _pending.Columns["first_seen"].DefaultCellStyle.Font = Theme.Mono;
        _pending.Columns["last_polled"].DefaultCellStyle.Font = Theme.Mono;
        // Both time columns now carry a date, so they need the width the minutes column can spare.
        _pending.Columns["ticket"].FillWeight = 85;
        _pending.Columns["minutes"].FillWeight = 45;
        _pending.Columns["first_seen"].FillWeight = 115;
        _pending.Columns["last_polled"].FillWeight = 115;

        _emptyPending = new Label
        {
            Text = "No pending worklogs — running clocks will appear here.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            Visible = false
        };

        var pendingCell = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 0)
        };
        pendingCell.Controls.Add(_pending);
        pendingCell.Controls.Add(_emptyPending);
        _pending.BringToFront();

        var submitNow  = new FlatButton { Text = "▶  Submit now",     Kind = ButtonKind.Accent, Width = 140 };
        var editSubmit = new FlatButton { Text = "✎  Edit && submit", Width = 160 };
        var discard    = new FlatButton { Text = "✕  Discard",        Kind = ButtonKind.Danger, Width = 120 };
        submitNow.Click  += (_, _) => OnSubmitNow();
        editSubmit.Click += (_, _) => OnEditSubmit();
        discard.Click    += (_, _) => OnDiscard();

        var actionsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 8, 12, 8)
        };
        foreach (var b in new[] { submitNow, editSubmit, discard })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            actionsFlow.Controls.Add(b);
        }

        // Separated by a wider left margin: the three above act on the selected worklog,
        // this one acts on the app, and they should not read as one group.
        var settings = new FlatButton { Text = "⚙  Settings", Width = 130 };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        settings.Margin = new Padding(28, 0, 0, 0);
        actionsFlow.Controls.Add(settings);

        var activityHeading = MakeHeading("Recent activity");
        _filter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130
        };
        _filter.Items.AddRange(new object[] { "All", "Activity", "Branch", "Jira", "Worklog" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => RenderEvents();

        var filterControls = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 12, 4),
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        filterControls.Controls.Add(new Label
        {
            Text = "Filter",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0)
        });
        filterControls.Controls.Add(_filter);

        var filterRowPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true
        };
        filterRowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRowPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filterRowPanel.Controls.Add(activityHeading, 0, 0);
        filterRowPanel.Controls.Add(filterControls, 1, 0);

        _events = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Margin = new Padding(12, 0, 12, 12)
        };
        _events.Columns.Add("Time",   90);
        _events.Columns.Add("Kind",   150);
        _events.Columns.Add("Detail", 560);

        var eventsCell = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 12)
        };
        eventsCell.Controls.Add(_events);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 0: header pills
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 1: pending heading
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));   // 2: pending grid / empty
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 3: actions
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 4: activity heading + filter
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 5: events list
        root.Controls.Add(headerFlow,     0, 0);
        root.Controls.Add(pendingHeading, 0, 1);
        root.Controls.Add(pendingCell,    0, 2);
        root.Controls.Add(actionsFlow,    0, 3);
        root.Controls.Add(filterRowPanel, 0, 4);
        root.Controls.Add(eventsCell,     0, 5);
        Controls.Add(root);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshFromDb();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            _subscriptionCts?.Cancel();
            _refreshTimer.Stop();
        };

        Theme.Apply(this);
    }

    private static StatusPill MakePill(string text, PillTone tone, bool dot, bool mono = false)
    {
        var pill = new StatusPill { ShowDot = dot };
        if (mono) pill.Font = Theme.Mono;
        pill.Set(text, tone);
        return pill;
    }

    private static Label MakeHeading(string text) => new()
    {
        Text = text,
        Font = Theme.Heading,
        ForeColor = Theme.TextPrimary,
        AutoSize = true,
        Padding = new Padding(12, 12, 12, 4)
    };

    public void ShowAndSubscribe()
    {
        if (Visible) { Activate(); return; }
        Show();
        BringToFront();
        _subscriptionCts = new CancellationTokenSource();
        var token = _subscriptionCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _bus.Subscribe(token).ConfigureAwait(false))
                {
                    if (IsDisposed) return;
                    BeginInvoke(() => OnEvent(evt));
                }
            }
            catch (OperationCanceledException) { }
        }, token);
        RefreshFromDb();
        _refreshTimer.Start();
    }

    private void OnEvent(DomainEvent evt)
    {
        _eventBuffer.AddFirst(evt);
        while (_eventBuffer.Count > 50) _eventBuffer.RemoveLast();
        RenderEvents();
        switch (evt)
        {
            case ActivityChanged a:
                if (a.State == UserActivityState.Active)
                    _statePill.Set("Active", PillTone.Active);
                else
                    _statePill.Set("Idle", PillTone.Idle);
                break;
            case BranchChanged b:
                _branchPill.Set(b.Branch ?? "no branch", PillTone.Neutral);
                _ticketPill.Set(b.TicketKey ?? "no ticket", b.TicketKey is null ? PillTone.Neutral : PillTone.Active);
                RefreshFromDb();
                break;
            case WorklogSubmitted:
                RefreshFromDb();
                break;
        }
    }

    private void RenderEvents()
    {
        var filter = _filter.SelectedItem as string ?? "All";
        bool Match(DomainEvent e) => filter switch
        {
            "Activity" => e is ActivityChanged,
            "Branch"   => e is BranchChanged,
            "Jira"     => e is JiraStatusTransition,
            "Worklog"  => e is WorklogSubmitted,
            _ => true
        };
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var e in _eventBuffer.Where(Match))
        {
            _events.Items.Add(new ListViewItem(new[]
            {
                e.AtUtc.ToLocalTime().ToString("HH:mm:ss"),
                e.GetType().Name,
                Describe(e)
            }));
        }
        _events.EndUpdate();
    }

    private static string Describe(DomainEvent e) => e switch
    {
        ActivityChanged a => a.State.ToString(),
        BranchChanged b   => $"{Path.GetFileName(b.RepoPath) ?? "?"}: {b.Branch ?? "(detached)"} → {b.TicketKey ?? "(no ticket)"}",
        JiraStatusTransition t => $"{t.TicketKey}: {t.FromStatus} → {t.ToStatus}",
        WorklogSubmitted w => $"{w.TicketKey}: posted {w.Minutes}m (id={w.WorklogId})",
        RememberEntriesObserved r => $"{r.Entries.Count} entries observed for {r.EntryDate}",
        AuthenticationRequired ar => ar.Reason,
        _ => e.ToString() ?? ""
    };

    private void RefreshFromDb()
    {
        UpdateClaudeBadge();
        var open = _tickets.GetAllOpen();
        _pending.Rows.Clear();
        foreach (var t in open)
        {
            _pending.Rows.Add(
                t.TicketKey,
                t.MinutesActive,
                t.CycleStarted.ToLocalTime().ToString(RowTimeFormat),
                // Seconds are still stored and still drive the staleness check below; they are
                // just noise in a column you read at a glance.
                t.LastPolled?.ToLocalTime().ToString(RowTimeFormat) ?? "—");
        }
        bool hasRows = _pending.Rows.Count > 0;
        _pending.Visible = hasRows;
        _emptyPending.Visible = !hasRows;
        if (!hasRows) _emptyPending.BringToFront();

        _authStatus.Text = _oauthState.Load() is not null ? "Auth: ✓" : "Auth: ✗ (open Settings)";
        var maxPoll = open.Select(o => o.LastPolled).Where(p => p is not null).Max();
        _lastPoll.Text = $"Last Jira poll: {(maxPoll?.ToLocalTime().ToString("HH:mm:ss") ?? "—")}";
        var cfg = _config.TryGet();
        if (cfg is not null && maxPoll is not null)
        {
            var nextIn = (int)(maxPoll.Value.AddSeconds(cfg.JiraPollIntervalSeconds) - DateTime.UtcNow).TotalSeconds;
            _nextPoll.Text = nextIn > 0 ? $"Next: in {nextIn}s" : "Next: due";
        }
        else
        {
            _nextPoll.Text = "Next: —";
        }
    }

    /// <summary>
    /// Counts every tracked repo Claude is working in, not just the focused one - sessions run in
    /// parallel now, and a badge that only watched the foreground repo read "idle" while an agent
    /// was busy next door.
    ///
    /// Deliberately the same rule the aggregator bills on, through <see cref="ClaudeRepoActivity"/>.
    /// While the two decided separately this badge read "Claude idle" for the whole of any tool
    /// call longer than a minute, which is exactly when it mattered most. The 60s window applies
    /// only to the transcript half, which is silent for the length of a tool call; a session
    /// latched to "busy" holds the badge for as long as that session actually runs.
    /// </summary>
    private void UpdateClaudeBadge()
    {
        var repos = _repos.GetAll();
        var writes = _claudeProbe.Snapshot();
        var busy = ClaudeSessionActivity.BusyRepos(
            _sessionProbe.Snapshot(), repos.Select(r => r.Path), _liveness.IsRunning);
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(60);
        var active = repos.Count(r => ClaudeRepoActivity.IsActive(r.Path, writes, busy, cutoff));

        _claudePill.Set(
            active switch
            {
                0 => "Claude idle",
                1 => "Claude active",
                _ => $"Claude active ×{active}"
            },
            active == 0 ? PillTone.Idle : PillTone.Claude);
    }

    private string? CurrentTicket() =>
        _pending.SelectedRows.Count == 0 ? null : _pending.SelectedRows[0].Cells["ticket"].Value as string;

    private void OnSubmitNow()
    {
        var key = CurrentTicket();
        if (key is null) return;
        try { SubmitObserved(key); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnEditSubmit()
    {
        var key = CurrentTicket();
        if (key is null) return;

        var cycle = _tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
        if (cycle is null) return;
        var unconsumed = _entries.GetUnconsumedForTicket(key);
        var description = WorklogDescriptionBuilder.Build(unconsumed);
        using var form = new WorklogEditForm(key, cycle.MinutesActive, description);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        try { Submit(cycle, form.SubmittedMinutes, form.Description, unconsumed); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnDiscard()
    {
        var key = CurrentTicket();
        if (key is null) return;
        var cycle = _tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
        if (cycle is null) return;

        var confirm = MessageBox.Show(this,
            $"Discard {cycle.MinutesActive} minute(s) on {key}? This cannot be undone.",
            "TmTimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _tickets.MarkSubmitted(cycle.Id, "discarded:" + Guid.NewGuid().ToString("N"), 0, _clock.UtcNow);
        RefreshFromDb();
    }

    private void SubmitObserved(string ticketKey)
    {
        var cycle = _tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == ticketKey);
        if (cycle is null) return;
        var unconsumed = _entries.GetUnconsumedForTicket(ticketKey);
        var description = WorklogDescriptionBuilder.Build(unconsumed);
        Submit(cycle, cycle.MinutesActive, description, unconsumed);
    }

    private void Submit(TicketCycle cycle, int minutes, string description,
                        IReadOnlyList<StoredRememberEntry> unconsumed)
    {
        var req = WorklogRequestFactory.Build(minutes, description, _clock.LocalNow);
        var resp = _api.PostWorklogAsync(cycle.TicketKey, req, CancellationToken.None).GetAwaiter().GetResult();
        _tickets.MarkSubmitted(cycle.Id, resp.Id, minutes, _clock.UtcNow);
        _entries.TagConsumed(unconsumed.Select(e => e.Id).ToList(), cycle.Id);
        _log.LogInformation("Worklog {Id} posted to {Ticket} ({Minutes}m)", resp.Id, cycle.TicketKey, minutes);
        RefreshFromDb();
    }

    private void ShowError(Exception ex)
    {
        _log.LogError(ex, "Submit action failed");
        var key = CurrentTicket() ?? "this ticket";
        var msg = ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                $"Jira returned 404 for {key}.\n\n" +
                "The ticket does not exist in your Jira project, or your account does not have permission to view it.\n\n" +
                "Verify the ticket key matches your project (browse to it in Jira). " +
                "If the branch name produced a phantom key, use Discard to clear the local time.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                $"Jira returned 403 for {key}.\n\nYour account does not have permission to log time on this ticket.",
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                "Jira returned 401 (unauthorized).\n\nYour OAuth token may have expired — open Settings and reconnect.",
            _ => $"Failed: {ex.Message}"
        };
        MessageBox.Show(this, msg, "TmTimeTracker",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
