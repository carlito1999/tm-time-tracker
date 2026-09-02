namespace TmTimeTracker.UI;

/// <summary>
/// An <see cref="IUserNotifier"/> that becomes live once the tray icon exists.
///
/// Kept as the fallback behind <see cref="WindowsToastNotifier"/>: a balloon cannot be marked
/// urgent, but it still works when toasts are unavailable.
///
/// Registered as a singleton so background services can depend on it at construction time, long
/// before the UI thread has built the NotifyIcon - and so the headless CLI modes, which have no
/// tray icon at all, simply do nothing instead of failing.
/// </summary>
public sealed class TrayBalloonNotifier : IUserNotifier
{
    private NotifyIcon? _icon;
    private SynchronizationContext? _uiContext;

    public void Attach(NotifyIcon icon, SynchronizationContext? uiContext)
    {
        _icon = icon;
        _uiContext = uiContext;
    }

    public void Show(string title, string body, bool urgent = false)
    {
        var icon = urgent ? ToolTipIcon.Warning : ToolTipIcon.Info;
        var target = _icon;
        if (target is null) return;

        void Display()
        {
            target.BalloonTipTitle = title;
            target.BalloonTipText = body;
            target.BalloonTipIcon = icon;
            target.ShowBalloonTip(8_000);
        }

        // Balloons must be raised on the UI thread; the worker calls this from a background task.
        var context = _uiContext;
        if (context is null) Display();
        else context.Post(_ => Display(), null);
    }
}
