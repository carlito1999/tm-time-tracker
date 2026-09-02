namespace TmTimeTracker.UI;

/// <summary>
/// An <see cref="IBalloonNotifier"/> that becomes live once the tray icon exists.
///
/// Registered as a singleton so background services can depend on it at construction time, long
/// before the UI thread has built the NotifyIcon - and so the headless CLI modes, which have no
/// tray icon at all, simply do nothing instead of failing.
/// </summary>
public sealed class TrayBalloonNotifier : IBalloonNotifier
{
    private NotifyIcon? _icon;
    private SynchronizationContext? _uiContext;

    public void Attach(NotifyIcon icon, SynchronizationContext? uiContext)
    {
        _icon = icon;
        _uiContext = uiContext;
    }

    public void Show(string title, string body, ToolTipIcon icon = ToolTipIcon.Info)
    {
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
