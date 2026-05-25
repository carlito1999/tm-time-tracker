using System.Diagnostics;

namespace TmTimeTracker.Platform;

public sealed class GitBranchProbe : IGitBranchProbe
{
    public string? GetCurrentBranch(string repoPath)
    {
        if (!Directory.Exists(repoPath)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
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
            if (p.ExitCode != 0 || string.IsNullOrEmpty(output) || output == "HEAD")
                return null;
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
