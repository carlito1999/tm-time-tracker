namespace TmTimeTracker.UI;

public interface IBalloonNotifier
{
    void Show(string title, string body, ToolTipIcon icon = ToolTipIcon.Info);
}

public sealed class BalloonNotifier : IBalloonNotifier
{
    private readonly NotifyIcon _icon;
    public BalloonNotifier(NotifyIcon icon) => _icon = icon;
    public void Show(string title, string body, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(8_000);
    }
}
