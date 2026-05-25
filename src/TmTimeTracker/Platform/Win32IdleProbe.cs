using System.Runtime.InteropServices;

namespace TmTimeTracker.Platform;

public sealed class Win32IdleProbe : IIdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    public long SecondsSinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var deltaMs = unchecked(GetTickCount() - info.dwTime);
        return deltaMs / 1000;
    }

    public bool IsSessionLocked()
    {
        const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero) return true;
        CloseDesktop(hDesk);
        return false;
    }
}
