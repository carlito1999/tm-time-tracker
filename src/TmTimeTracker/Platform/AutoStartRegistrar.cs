using Microsoft.Win32;

namespace TmTimeTracker.Platform;

public static class AutoStartRegistrar
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TmTimeTracker";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: false);
        return k?.GetValue(ValueName) is string;
    }

    public static void Register(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true)
                   ?? Registry.CurrentUser.CreateSubKey(Key);
        k!.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    public static void Unregister()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
