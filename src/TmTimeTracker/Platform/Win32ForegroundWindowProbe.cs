using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TmTimeTracker.Platform;

public sealed class Win32ForegroundWindowProbe : IForegroundWindowProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public ForegroundWindow? Probe()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            var length = GetWindowTextLengthW(hWnd);
            if (length <= 0) return null;
            var sb = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return null;

            GetWindowThreadProcessId(hWnd, out var pid);
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = ""; }

            return new ForegroundWindow(processName, title);
        }
        catch
        {
            return null;
        }
    }
}
