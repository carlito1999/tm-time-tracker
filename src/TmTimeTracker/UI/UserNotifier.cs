using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TmTimeTracker.UI;

public interface IUserNotifier
{
    /// <param name="urgent">
    /// Sends the notification as a Windows "important notification", which is allowed to break
    /// through Do Not Disturb once the user permits it. Windows asks them that question itself,
    /// the first time an urgent notification arrives.
    /// </param>
    void Show(string title, string body, bool urgent = false);
}

/// <summary>
/// Raises a real Windows toast rather than a tray balloon, because a balloon cannot be marked
/// urgent and so cannot reach the user while Do Not Disturb is on.
///
/// Uses ToastNotificationManagerCompat, which is the path that works for an unpackaged Win32 app
/// like this one: it registers an AppUserModelID and COM activator under HKCU on the first send,
/// so there is nothing for the user to install and no Start menu shortcut to create.
/// </summary>
public sealed class WindowsToastNotifier : IUserNotifier
{
    private readonly IUserNotifier _fallback;
    private readonly ILogger<WindowsToastNotifier> _log;

    public WindowsToastNotifier(TrayBalloonNotifier fallback, ILogger<WindowsToastNotifier> log)
    {
        _fallback = fallback;
        _log = log;
    }

    public void Show(string title, string body, bool urgent = false)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildXml(title, body, urgent));
            ToastNotificationManagerCompat.CreateToastNotifier().Show(new ToastNotification(xml));
        }
        catch (Exception ex)
        {
            // Notifications can be switched off per-app or by policy, and the COM registration can
            // fail on a locked-down machine. A plain balloon is worse than a toast but far better
            // than swallowing the one message telling the user to act.
            _log.LogWarning(ex, "Toast notification failed; falling back to the tray balloon");
            _fallback.Show(title, body, urgent);
        }
    }

    /// <summary>
    /// Built as raw XML on purpose: the toolkit's ToastScenario enum predates "urgent" and offers
    /// only Default, Alarm, Reminder and IncomingCall, so ToastContentBuilder cannot express the
    /// one attribute this notification exists to set.
    /// </summary>
    internal static string BuildXml(string title, string body, bool urgent)
    {
        var scenario = urgent ? " scenario=\"urgent\"" : string.Empty;
        return $"""
            <toast{scenario}>
              <visual>
                <binding template="ToastGeneric">
                  <text>{Escape(title)}</text>
                  <text>{Escape(body)}</text>
                </binding>
              </visual>
            </toast>
            """;
    }

    private static string Escape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
