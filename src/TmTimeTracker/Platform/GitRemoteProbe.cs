using System.Diagnostics;

namespace TmTimeTracker.Platform;

public sealed class GitRemoteProbe : IGitRemoteProbe
{
    public string? GetRemoteUrl(string repoPath)
    {
        if (!Directory.Exists(repoPath)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "remote get-url origin",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            // A repo with no origin exits non-zero, which is an ordinary answer here, not a fault.
            return p.ExitCode != 0 || string.IsNullOrEmpty(output) ? null : output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
