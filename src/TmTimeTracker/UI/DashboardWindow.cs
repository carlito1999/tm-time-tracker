using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI;

public sealed class DashboardWindow : Form
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<DashboardWindow> _log;
    private readonly Label _stateBadge, _branchLabel, _ticketLabel;
    private readonly DataGridView _pending;
    private readonly ListView _events;
    private readonly ComboBox _filter;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _authStatus, _lastPoll, _nextPoll;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly LinkedList<DomainEvent> _eventBuffer = new();
    private CancellationTokenSource? _subscriptionCts;

    public DashboardWindow(IServiceProvider sp, ILogger<DashboardWindow> log)
    {
        _sp = sp; _log = log;
        Text = "TmTimeTracker — Dashboard";
        Width = 800; Height = 600;
        StartPosition = FormStartPosition.CenterScreen;

        var top = new Panel { Top = 0, Left = 0, Width = 800, Height = 40, Dock = DockStyle.Top };
        _stateBadge = new Label
        {
            Top = 10, Left = 10, AutoSize = true, Text = "● —",
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        _branchLabel = new Label { Top = 12, Left = 160, AutoSize = true, Text = "Branch: —" };
        _ticketLabel = new Label { Top = 12, Left = 440, AutoSize = true, Text = "Ticket: —" };
        top.Controls.AddRange(new Control[] { _stateBadge, _branchLabel, _ticketLabel });
        Controls.Add(top);

        _pending = new DataGridView
        {
            Top = 40, Left = 0, Width = 800, Height = 220, Dock = DockStyle.Top,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false
        };
        _pending.Columns.Add("ticket", "Ticket");
        _pending.Columns.Add("minutes", "Minutes");
        _pending.Columns.Add("first_seen", "First seen");
        _pending.Columns.Add("last_polled", "Last Jira poll");
        Controls.Add(_pending);

        var actions = new Panel { Top = 260, Left = 0, Width = 800, Height = 36, Dock = DockStyle.Top };
        var submitNow = new Button { Top = 5, Left = 10, Width = 110, Text = "Submit now" };
        var editSubmit = new Button { Top = 5, Left = 130, Width = 130, Text = "Edit && submit" };
        var discard = new Button { Top = 5, Left = 270, Width = 90, Text = "Discard" };
        submitNow.Click += (_, _) => OnSubmitNow();
        editSubmit.Click += (_, _) => OnEditSubmit();
        discard.Click += (_, _) => OnDiscard();
        actions.Controls.AddRange(new Control[] { submitNow, editSubmit, discard });
        Controls.Add(actions);

        var filterPanel = new Panel { Top = 296, Left = 0, Width = 800, Height = 28, Dock = DockStyle.Top };
        filterPanel.Controls.Add(new Label { Top = 5, Left = 10, AutoSize = true, Text = "Recent events  Filter:" });
        _filter = new ComboBox
        {
            Top = 2, Left = 160, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList
        };
        _filter.Items.AddRange(new object[] { "All", "Activity", "Branch", "Jira", "Worklog" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => RenderEvents();
        filterPanel.Controls.Add(_filter);
        Controls.Add(filterPanel);

        _events = new ListView
        {
            Top = 324, Left = 0, Width = 800, Height = 200, Dock = DockStyle.Top,
            View = View.Details, FullRowSelect = true
        };
        _events.Columns.Add("Time", 80);
        _events.Columns.Add("Kind", 110);
        _events.Columns.Add("Detail", 600);
        Controls.Add(_events);

        _statusBar = new StatusStrip();
        _authStatus = new ToolStripStatusLabel("Auth: —");
        _lastPoll = new ToolStripStatusLabel("Last Jira poll: —");
        _nextPoll = new ToolStripStatusLabel("Next: —");
        _statusBar.Items.AddRange(new ToolStripItem[] { _authStatus, _lastPoll, _nextPoll });
        Controls.Add(_statusBar);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshFromDb();

        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            _subscriptionCts?.Cancel();
            _refreshTimer.Stop();
        };
    }

    public void ShowAndSubscribe()
    {
        if (Visible) { Activate(); return; }
        Show();
        BringToFront();
        _subscriptionCts = new CancellationTokenSource();
        var bus = _sp.GetRequiredService<IEventBus>();
        var token = _subscriptionCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in bus.Subscribe(token).ConfigureAwait(false))
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
                _stateBadge.Text = a.State == UserActivityState.Active ? "● ACTIVE" : "● IDLE";
                _stateBadge.ForeColor = a.State == UserActivityState.Active ? Color.DarkGreen : Color.DarkGray;
                break;
            case BranchChanged b:
                _branchLabel.Text = $"Branch: {b.Branch ?? "(none)"}";
                _ticketLabel.Text = $"Ticket: {b.TicketKey ?? "(none)"}";
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
        BranchChanged b   => $"{b.Branch ?? "(detached)"} → {b.TicketKey ?? "(no ticket)"}",
        JiraStatusTransition t => $"{t.TicketKey}: {t.FromStatus} → {t.ToStatus}",
        WorklogSubmitted w => $"{w.TicketKey}: posted {w.Minutes}m (id={w.WorklogId})",
        RememberEntriesObserved r => $"{r.Entries.Count} entries observed for {r.EntryDate}",
        AuthenticationRequired ar => ar.Reason,
        _ => e.ToString() ?? ""
    };

    private void RefreshFromDb()
    {
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthStateRepository>();
        var open = tickets.GetAllOpen();
        _pending.Rows.Clear();
        foreach (var t in open)
        {
            _pending.Rows.Add(t.TicketKey, t.MinutesActive,
                t.CycleStarted.ToLocalTime().ToString("HH:mm"),
                t.LastPolled?.ToLocalTime().ToString("HH:mm:ss") ?? "—");
        }
        _authStatus.Text = oauth.Load() is not null ? "Auth: ✓" : "Auth: ✗ (open Settings)";
        var maxPoll = open.Select(o => o.LastPolled).Where(p => p is not null).Max();
        _lastPoll.Text = $"Last Jira poll: {(maxPoll?.ToLocalTime().ToString("HH:mm:ss") ?? "—")}";
        var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>().TryGet();
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

        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
        var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
        if (cycle is null) return;
        var unconsumed = entries.GetUnconsumedForTicket(key);
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
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
        if (cycle is null) return;

        var confirm = MessageBox.Show(this,
            $"Discard {cycle.MinutesActive} minute(s) on {key}? This cannot be undone.",
            "TmTimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        tickets.MarkSubmitted(cycle.Id, "discarded:" + Guid.NewGuid().ToString("N"), 0, clock.UtcNow);
        RefreshFromDb();
    }

    private void SubmitObserved(string ticketKey)
    {
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
        var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == ticketKey);
        if (cycle is null) return;
        var unconsumed = entries.GetUnconsumedForTicket(ticketKey);
        var description = WorklogDescriptionBuilder.Build(unconsumed);
        Submit(cycle, cycle.MinutesActive, description, unconsumed);
    }

    private void Submit(TicketCycle cycle, int minutes, string description,
                        IReadOnlyList<StoredRememberEntry> unconsumed)
    {
        using var scope = _sp.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<TmTimeTracker.Jira.JiraApiClient>();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var entriesRepo = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var req = WorklogRequestFactory.Build(minutes, description, clock.LocalNow);
        var resp = api.PostWorklogAsync(cycle.TicketKey, req, CancellationToken.None).GetAwaiter().GetResult();
        tickets.MarkSubmitted(cycle.Id, resp.Id, minutes, clock.UtcNow);
        entriesRepo.TagConsumed(unconsumed.Select(e => e.Id).ToList(), cycle.Id);
        _log.LogInformation("Worklog {Id} posted to {Ticket} ({Minutes}m)", resp.Id, cycle.TicketKey, minutes);
        RefreshFromDb();
    }

    private void ShowError(Exception ex)
    {
        _log.LogError(ex, "Submit action failed");
        MessageBox.Show(this, $"Failed: {ex.Message}", "TmTimeTracker",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
