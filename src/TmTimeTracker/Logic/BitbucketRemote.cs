namespace TmTimeTracker.Logic;

public sealed record BitbucketRepo(string Workspace, string Slug);

/// <summary>
/// Works out which Bitbucket repository a local clone points at, from its git remote.
///
/// This is what lets the app query Bitbucket without any per-repo configuration: a tracked repo is
/// just a path, and the remote already says everything needed. Repos hosted anywhere else return
/// null and are simply skipped.
/// </summary>
public static class BitbucketRemote
{
    public static BitbucketRepo? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

        var url = remoteUrl.Trim();
        var path = ScpLikePath(url) ?? HttpPath(url);
        if (path is null) return null;

        // "workspace/repo.git" - anything deeper is a Bitbucket Server style path this does not serve.
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        var slug = parts[1];
        if (slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            slug = slug[..^4];

        return string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(slug)
            ? null
            : new BitbucketRepo(parts[0], slug);
    }

    /// <summary>git@bitbucket.org:workspace/repo.git - not a URI, so Uri cannot parse it.</summary>
    private static string? ScpLikePath(string url)
    {
        var at = url.IndexOf('@');
        var colon = url.IndexOf(':');
        if (at < 0 || colon < at) return null;
        if (url.Contains("://", StringComparison.Ordinal)) return null;

        var host = url[(at + 1)..colon];
        return Host(host) ? url[(colon + 1)..] : null;
    }

    /// <summary>
    /// https://user@bitbucket.org/workspace/repo.git - the userinfo is common in clone URLs copied
    /// out of Bitbucket and must not be mistaken for part of the path.
    /// </summary>
    private static string? HttpPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "ssh")) return null;
        return Host(uri.Host) ? uri.AbsolutePath : null;
    }

    private static bool Host(string host) =>
        host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase);
}
