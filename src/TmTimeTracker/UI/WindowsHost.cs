using Microsoft.Extensions.Logging;

namespace TmTimeTracker.UI;

public sealed class WindowsHost
{
    private readonly IServiceProvider _sp;
    private readonly ILoggerFactory _logFactory;
    private SetupWindow? _setup;
    private DashboardWindow? _dashboard;
    private SynchronizationContext? _uiCtx;

    public WindowsHost(IServiceProvider sp, ILoggerFactory logFactory)
    {
        _sp = sp; _logFactory = logFactory;
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

    public void ShowDashboard()
    {
        Marshal(() =>
        {
            if (_dashboard is null || _dashboard.IsDisposed)
                _dashboard = new DashboardWindow(_sp, _logFactory.CreateLogger<DashboardWindow>());
            _dashboard.ShowAndSubscribe();
        });
    }

    private void Marshal(Action action)
    {
        if (_uiCtx is null) action();
        else _uiCtx.Post(_ => action(), null);
    }
}
