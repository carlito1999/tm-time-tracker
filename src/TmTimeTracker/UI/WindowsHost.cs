using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI;

public sealed class WindowsHost
{
    private readonly IServiceProvider _sp;
    private readonly ILoggerFactory _logFactory;
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
    private readonly WeeklyReportExporter _exporter;

    private SetupWindow? _setup;
    private SettingsWindow? _settings;
    private DashboardWindow? _dashboard;
    private ExportReportForm? _export;
    private SynchronizationContext? _uiCtx;

    public WindowsHost(
        IServiceProvider sp,
        ILoggerFactory logFactory,
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
        WeeklyReportExporter exporter)
    {
        _sp = sp;
        _logFactory = logFactory;
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
        _exporter = exporter;
    }

    public void RegisterUiContext(SynchronizationContext uiCtx) => _uiCtx = uiCtx;

    public event Action? SetupCompleted;

    public void ShowSetup(SetupWindow.Page startPage = SetupWindow.Page.OAuthApp)
    {
        Marshal(() =>
        {
            if (_setup is null || _setup.IsDisposed)
            {
                _setup = new SetupWindow(_sp, _logFactory.CreateLogger<SetupWindow>(), startPage);
                _setup.SetupCompleted += () => SetupCompleted?.Invoke();
                _setup.FormClosed += (_, _) => _setup = null;
            }
            _setup.Show();
            _setup.BringToFront();
            _setup.Activate();
        });
    }

    public void ShowSettings()
    {
        Marshal(() =>
        {
            if (_oauthState.Load() is null)
            {
                ShowSetupInternal();
                return;
            }
            if (_settings is null || _settings.IsDisposed)
            {
                _settings = new SettingsWindow(_sp, _logFactory.CreateLogger<SettingsWindow>());
                _settings.FormClosed += (_, _) => _settings = null;
            }
            _settings.Show();
            _settings.BringToFront();
            _settings.Activate();
        });
    }

    private void ShowSetupInternal()
    {
        if (_setup is null || _setup.IsDisposed)
        {
            _setup = new SetupWindow(_sp, _logFactory.CreateLogger<SetupWindow>(), SetupWindow.Page.OAuthApp);
            _setup.SetupCompleted += () => SetupCompleted?.Invoke();
            _setup.FormClosed += (_, _) => _setup = null;
        }
        _setup.Show();
        _setup.BringToFront();
        _setup.Activate();
    }

    public void ShowDashboard()
    {
        Marshal(() =>
        {
            if (_dashboard is null || _dashboard.IsDisposed)
            {
                _dashboard = new DashboardWindow(
                    _bus, _tickets, _oauthState, _entries, _config,
                    _clock, _claudeProbe, _sessionProbe, _liveness, _repos, _api,
                    _logFactory.CreateLogger<DashboardWindow>());
                // Subscribed once at construction, not per Show, or the handler would stack up.
                _dashboard.SettingsRequested += ShowSettings;
            }
            _dashboard.ShowAndSubscribe();
        });
    }

    public void ShowExport()
    {
        Marshal(() =>
        {
            if (_export is null || _export.IsDisposed)
            {
                _export = new ExportReportForm(_exporter, _logFactory.CreateLogger<ExportReportForm>());
                _export.FormClosed += (_, _) => _export = null;
            }
            _export.Show();
            _export.BringToFront();
            _export.Activate();
        });
    }

    private void Marshal(Action action)
    {
        if (_uiCtx is null) action();
        else _uiCtx.Post(_ => action(), null);
    }
}
