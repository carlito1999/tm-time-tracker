using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI.SettingsPages;

public sealed class ConnectionPage : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly ILogger _log;
    private readonly Label _currentSiteLabel;
    private readonly Label _currentCloudIdLabel;
    private readonly FlowLayoutPanel _sitesPanel;
    private readonly Label _sitesEmpty;
    private readonly FlatButton _switchBtn;
    private readonly FlatButton _authorizeMoreBtn;
    private readonly FlatButton _refreshBtn;
    private readonly FlatButton _disconnectBtn;
    private readonly Label _toast;

    private readonly List<RadioButton> _siteRadios = new();
    private string? _currentCloudId;

    public event Action? StateChanged;

    public ConnectionPage(IServiceProvider sp, ILogger log)
    {
        _sp = sp;
        _log = log;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;

        var currentHeading = MakeHeading("Current connection");
        _currentSiteLabel = new Label
        {
            Text = "—",
            Font = Theme.Mono,
            ForeColor = Theme.Accent,
            AutoSize = true,
            Padding = new Padding(16, 0, 16, 0)
        };
        _currentCloudIdLabel = new Label
        {
            Text = "",
            Font = Theme.Mono,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Padding = new Padding(16, 2, 16, 12)
        };

        var sitesHeading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true
        };
        sitesHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sitesHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sitesHeading.Controls.Add(MakeHeading("Available sites"), 0, 0);
        _refreshBtn = new FlatButton { Text = "↻ Refresh", Width = 110 };
        _refreshBtn.Margin = new Padding(0, 12, 16, 4);
        _refreshBtn.Click += async (_, _) => await ReloadSitesAsync(showToastIfSame: true);
        sitesHeading.Controls.Add(_refreshBtn, 1, 0);

        _sitesPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16, 0, 16, 8)
        };
        _sitesEmpty = new Label
        {
            Text = "Loading…",
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        _sitesPanel.Controls.Add(_sitesEmpty);

        _switchBtn = new FlatButton { Text = "Switch to selected", Kind = ButtonKind.Accent, Width = 180, Enabled = false };
        _switchBtn.Margin = new Padding(16, 4, 0, 12);
        _switchBtn.Click += (_, _) => OnSwitch();

        var moreHeading = MakeHeading("Don't see your site here?");
        var moreHelp = new Label
        {
            Text = "The OAuth app needs consent for each site. Authorizing again shows Atlassian's site picker — tick the additional workspaces.",
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(16, 0, 16, 4),
            Dock = DockStyle.Fill,
            MaximumSize = new Size(620, 0)
        };
        _authorizeMoreBtn = new FlatButton { Text = "+ Authorize more sites", Width = 200 };
        _authorizeMoreBtn.Margin = new Padding(16, 4, 0, 16);
        _authorizeMoreBtn.Click += async (_, _) => await OnAuthorizeMore();

        var dangerHeading = MakeHeading("Troubleshooting");
        _disconnectBtn = new FlatButton { Text = "⨯  Disconnect and start over", Kind = ButtonKind.Danger, Width = 240 };
        _disconnectBtn.Margin = new Padding(16, 4, 0, 16);
        _disconnectBtn.Click += (_, _) => OnDisconnect();

        _toast = new Label
        {
            Text = "",
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            AutoSize = false,
            Height = 22,
            Padding = new Padding(16, 4, 16, 4),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, SizeType st = SizeType.AutoSize, int? abs = null)
        {
            root.RowStyles.Add(new RowStyle(st, abs ?? 0));
            root.Controls.Add(c, 0, root.RowCount);
            root.RowCount++;
        }
        AddRow(currentHeading);
        AddRow(_currentSiteLabel);
        AddRow(_currentCloudIdLabel);
        AddRow(sitesHeading);
        AddRow(_sitesPanel);
        AddRow(_switchBtn);
        AddRow(moreHeading);
        AddRow(moreHelp);
        AddRow(_authorizeMoreBtn);
        AddRow(dangerHeading);
        AddRow(_disconnectBtn);
        AddRow(_toast);
        Controls.Add(root);

        _ = ReloadSitesAsync(showToastIfSame: false);
    }

    public void LoadCurrentState()
    {
        var s = _sp.GetRequiredService<OAuthStateRepository>().Load();
        _currentCloudId = s?.CloudId;
        if (s is null)
        {
            _currentSiteLabel.Text = "Not connected";
            _currentSiteLabel.ForeColor = Theme.TextSecondary;
            _currentCloudIdLabel.Text = "";
        }
        else
        {
            _currentCloudIdLabel.Text = $"cloud_id  {s.CloudId}";
        }
    }

    private async Task ReloadSitesAsync(bool showToastIfSame)
    {
        LoadCurrentState();
        ToggleAll(false);
        _toast.Text = "Loading available sites…";
        try
        {
            var coord = _sp.GetRequiredService<OAuthCoordinator>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var sites = await coord.ListAccessibleAsync(cts.Token).ConfigureAwait(true);
            RenderSites(sites);
            UpdateCurrentSiteLabel(sites);
            _toast.Text = showToastIfSame ? $"Refreshed — {sites.Count} site(s) accessible." : "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to list accessible Atlassian sites");
            _sitesPanel.Controls.Clear();
            _sitesPanel.Controls.Add(new Label
            {
                Text = $"Could not load sites: {ex.Message}",
                ForeColor = Theme.Danger,
                AutoSize = true
            });
            _toast.Text = "";
        }
        finally
        {
            ToggleAll(true);
        }
    }

    private void RenderSites(IReadOnlyList<AtlassianResource> sites)
    {
        _sitesPanel.Controls.Clear();
        _siteRadios.Clear();
        if (sites.Count == 0)
        {
            _sitesPanel.Controls.Add(new Label
            {
                Text = "No accessible sites. Try Authorize more sites.",
                ForeColor = Theme.TextSecondary,
                AutoSize = true
            });
            _switchBtn.Enabled = false;
            return;
        }
        foreach (var s in sites)
        {
            var isCurrent = s.Id == _currentCloudId;
            var rb = new RadioButton
            {
                Text = s.Url + (isCurrent ? "    (current)" : ""),
                Tag = s.Id,
                Checked = isCurrent,
                Font = Theme.Mono,
                ForeColor = isCurrent ? Theme.Accent : Theme.TextPrimary,
                AutoSize = true,
                Padding = new Padding(0, 2, 0, 2)
            };
            rb.CheckedChanged += (_, _) => UpdateSwitchEnabled();
            _siteRadios.Add(rb);
            _sitesPanel.Controls.Add(rb);
        }
        UpdateSwitchEnabled();
    }

    private void UpdateSwitchEnabled()
    {
        var sel = _siteRadios.FirstOrDefault(r => r.Checked);
        _switchBtn.Enabled = sel != null && (string?)sel.Tag != _currentCloudId;
    }

    private void UpdateCurrentSiteLabel(IReadOnlyList<AtlassianResource> sites)
    {
        var match = sites.FirstOrDefault(s => s.Id == _currentCloudId);
        if (match is not null)
        {
            _currentSiteLabel.Text = match.Url;
            _currentSiteLabel.ForeColor = Theme.Accent;
        }
        else if (_currentCloudId is not null)
        {
            _currentSiteLabel.Text = $"(saved cloud_id not in current grant — reconnect needed)";
            _currentSiteLabel.ForeColor = Theme.Danger;
        }
        else
        {
            _currentSiteLabel.Text = "Not connected";
            _currentSiteLabel.ForeColor = Theme.TextSecondary;
        }
    }

    private void OnSwitch()
    {
        var sel = _siteRadios.FirstOrDefault(r => r.Checked);
        if (sel is null) return;
        var newCloudId = (string)sel.Tag!;
        try
        {
            _sp.GetRequiredService<OAuthCoordinator>().SwitchCloudId(newCloudId);
            _currentCloudId = newCloudId;
            _currentCloudIdLabel.Text = $"cloud_id  {newCloudId}";
            var url = sel.Text.Replace("    (current)", "");
            _currentSiteLabel.Text = url;
            _currentSiteLabel.ForeColor = Theme.Accent;
            foreach (var rb in _siteRadios)
            {
                var isNowCurrent = (string?)rb.Tag == newCloudId;
                rb.Text = (rb.Text.Replace("    (current)", "")) + (isNowCurrent ? "    (current)" : "");
                rb.ForeColor = isNowCurrent ? Theme.Accent : Theme.TextPrimary;
            }
            UpdateSwitchEnabled();
            _toast.Text = $"Switched to {url}.";
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SwitchCloudId failed");
            _toast.Text = $"Switch failed: {ex.Message}";
        }
    }

    private async Task OnAuthorizeMore()
    {
        ToggleAll(false);
        _toast.Text = "Opening browser — approve and select sites on Atlassian…";
        try
        {
            var oauth = _sp.GetRequiredService<JiraOAuthClient>();
            var listener = _sp.GetRequiredService<LocalCallbackListener>();
            var coord = _sp.GetRequiredService<OAuthCoordinator>();
            var cfgSrc = _sp.GetRequiredService<IOAuthAppConfigSource>();

            var state = Guid.NewGuid().ToString("N");
            var url = oauth.BuildAuthorizationUrl(state);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            var redirect = new Uri(cfgSrc.Get().RedirectUri);
            var prefix = $"{redirect.Scheme}://{redirect.Authority}/";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var cb = await listener.ListenOnceAsync(prefix, cts.Token).ConfigureAwait(true);
            if (cb.State != state) throw new InvalidOperationException("OAuth state mismatch.");
            await coord.ReauthorizeAsync(cb.Code, cts.Token).ConfigureAwait(true);
            _toast.Text = "Reauthorized. Refreshing site list…";
            await ReloadSitesAsync(showToastIfSame: false).ConfigureAwait(true);
            _toast.Text = "Done. Pick a site and click Switch.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Authorize more sites failed");
            _toast.Text = $"Authorize failed: {ex.Message}";
        }
        finally
        {
            ToggleAll(true);
        }
    }

    private void OnDisconnect()
    {
        var ok = MessageBox.Show(this,
            "Disconnect will clear your saved tokens. You'll need to re-run setup to use the app.\n\nContinue?",
            "TmTimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes) return;
        try
        {
            _sp.GetRequiredService<OAuthCoordinator>().Disconnect();
            _currentCloudId = null;
            _sitesPanel.Controls.Clear();
            _siteRadios.Clear();
            _currentSiteLabel.Text = "Not connected";
            _currentSiteLabel.ForeColor = Theme.TextSecondary;
            _currentCloudIdLabel.Text = "";
            _switchBtn.Enabled = false;
            _toast.Text = "Disconnected. Close Settings and run setup again from the tray.";
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Disconnect failed");
            _toast.Text = $"Disconnect failed: {ex.Message}";
        }
    }

    private void ToggleAll(bool enabled)
    {
        _switchBtn.Enabled = enabled && _switchBtn.Enabled;
        _authorizeMoreBtn.Enabled = enabled;
        _refreshBtn.Enabled = enabled;
        _disconnectBtn.Enabled = enabled;
        foreach (var rb in _siteRadios) rb.Enabled = enabled;
    }

    private static Label MakeHeading(string text) => new()
    {
        Text = text,
        Font = Theme.Heading,
        ForeColor = Theme.TextPrimary,
        AutoSize = true,
        Padding = new Padding(16, 12, 16, 4)
    };
}
