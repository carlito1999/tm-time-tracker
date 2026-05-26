namespace TmTimeTracker.Logic;

public static class ClaudeProjectSlug
{
    // Empirically derived from Claude Code's own folder-naming convention under
    // %USERPROFILE%\.claude\projects\: each of ':', '\\', '/', '.' becomes '-'.
    // Case is preserved (Claude Code does not lowercase). Lookup against
    // snapshots should use a case-insensitive comparer since Windows paths are
    // case-insensitive.
    private static readonly char[] Separators = { ':', '\\', '/', '.' };

    public static string FromPath(string? repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return "";
        var chars = repoPath.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Separators, chars[i]) >= 0)
                chars[i] = '-';
        }
        return new string(chars);
    }
}
