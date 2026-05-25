namespace TmTimeTracker.Configuration;

public static class AppPaths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TmTimeTracker");

    public static string DatabasePath => Path.Combine(DataDir, "state.db");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
