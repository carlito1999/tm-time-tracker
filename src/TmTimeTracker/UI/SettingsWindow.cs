using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.UI.SettingsPages;
using TmTimeTracker.UI.SetupPages;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI;

public sealed class SettingsWindow : Form
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SettingsWindow> _log;

    private readonly FlatButton _tabConnection;
    private readonly FlatButton _tabRepositories;
    private readonly FlatButton _tabOAuthApp;
    private readonly FlatButton _tabSlack;
    private readonly Panel _content;

    private readonly Lazy<Control> _connectionPage;
    private readonly Lazy<Control> _oauthAppPage;
    private readonly Lazy<Control> _repositoriesPage;
    private readonly Lazy<Control> _slackPage;

    private FlatButton _activeTab;

    public SettingsWindow(IServiceProvider sp, ILogger<SettingsWindow> log)
    {
        _sp = sp;
        _log = log;

        Text = "TmTimeTracker — Settings";
        Icon = AppIcon.Load();
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _connectionPage   = new Lazy<Control>(() => new ConnectionPage(_sp, _log));
        _oauthAppPage     = new Lazy<Control>(BuildOAuthAppTab);
        _repositoriesPage = new Lazy<Control>(BuildRepositoriesTab);
        _slackPage        = new Lazy<Control>(() => new SlackPage(_sp, _log));

        _tabConnection   = MakeTabButton("Connection");
        _tabRepositories = MakeTabButton("Repositories");
        _tabOAuthApp     = MakeTabButton("OAuth app");
        _tabSlack        = MakeTabButton("Slack");
        _tabConnection.Click   += (_, _) => Activate(_tabConnection,   _connectionPage.Value);
        _tabRepositories.Click += (_, _) => Activate(_tabRepositories, _repositoriesPage.Value);
        _tabOAuthApp.Click     += (_, _) => Activate(_tabOAuthApp,     _oauthAppPage.Value);
        _tabSlack.Click        += (_, _) => Activate(_tabSlack,        _slackPage.Value);

        var tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 12, 12, 4)
        };
        foreach (var t in new[] { _tabConnection, _tabRepositories, _tabSlack, _tabOAuthApp })
        {
            t.Margin = new Padding(0, 0, 8, 0);
            tabBar.Controls.Add(t);
        }

        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };

        var closeBtn = new FlatButton { Text = "Close", Width = 100 };
        closeBtn.Margin = new Padding(0, 8, 16, 8);
        closeBtn.Click += (_, _) => Close();
        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4)
        };
        bottomBar.Controls.Add(closeBtn);
        var bottomDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };

        Controls.Add(_content);
        Controls.Add(bottomDivider);
        Controls.Add(bottomBar);
        Controls.Add(divider);
        Controls.Add(tabBar);

        _activeTab = _tabConnection;
        Activate(_tabConnection, _connectionPage.Value);

        Theme.Apply(this);
    }

    private static FlatButton MakeTabButton(string text) =>
        new() { Text = text, Width = 140, Kind = ButtonKind.Default };

    private Control BuildOAuthAppTab()
    {
        var page = new OAuthAppPage { Dock = DockStyle.Fill };
        var existing = _sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        page.LoadConfig(existing);

        var toast = MakeToast("Changing these requires reconnecting on the Connection tab.");
        var save = new FlatButton { Text = "Save", Kind = ButtonKind.Accent, Width = 100, Enabled = page.IsValid };
        save.Margin = new Padding(0, 8, 16, 8);
        page.ValidationChanged += () => save.Enabled = page.IsValid;
        save.Click += (_, _) =>
        {
            try
            {
                _sp.GetRequiredService<OAuthAppConfigRepository>().Save(page.BuildConfig());
                toast.Text = "Saved. Use Connection tab to reconnect with new credentials.";
                toast.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OAuth app save failed");
                toast.Text = $"Save failed: {ex.Message}";
                toast.ForeColor = Theme.Danger;
            }
        };
        return WrapWithSaveBar(page, save, toast);
    }

    private Control BuildRepositoriesTab()
    {
        var page = new PathsPage(_sp) { Dock = DockStyle.Fill };
        var toast = MakeToast("Add the repos you want to track. Click Save to persist.");
        var save = new FlatButton { Text = "Save", Kind = ButtonKind.Accent, Width = 100, Enabled = page.IsValid };
        save.Margin = new Padding(0, 8, 16, 8);
        page.StateChanged += () => save.Enabled = page.IsValid;
        save.Click += (_, _) =>
        {
            try
            {
                _sp.GetRequiredService<ConfigRepository>().Update(page.BuildConfig());
                toast.Text = "Saved.";
                toast.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Paths save failed");
                toast.Text = $"Save failed: {ex.Message}";
                toast.ForeColor = Theme.Danger;
            }
        };
        return WrapWithSaveBar(page, save, toast);
    }

    private static Label MakeToast(string initial) => new()
    {
        Text = initial,
        ForeColor = Theme.TextSecondary,
        Font = Theme.Body,
        AutoSize = false,
        Height = 22,
        Dock = DockStyle.Bottom,
        Padding = new Padding(16, 4, 16, 4)
    };

    private static Control WrapWithSaveBar(Control page, FlatButton save, Label toast)
    {
        var saveBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 4, 0, 4)
        };
        saveBar.Controls.Add(save);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(page);
        host.Controls.Add(toast);
        host.Controls.Add(saveBar);
        return host;
    }

    private void Activate(FlatButton tab, Control page)
    {
        _activeTab.Kind = ButtonKind.Default;
        _activeTab.Invalidate();
        _activeTab = tab;
        _activeTab.Kind = ButtonKind.Accent;
        _activeTab.Invalidate();

        _content.SuspendLayout();
        _content.Controls.Clear();
        page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);
        Theming.Theme.Apply(this);
        _content.ResumeLayout();
    }
}
