using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;

namespace TmTimeTracker.Services;

public interface IJiraSiteResolver
{
    Task<string?> GetSiteUrlAsync(CancellationToken ct);
}

/// <summary>
/// Resolves the Jira site URL lazily and self-heals for installs that authenticated before this
/// existed. OAuthCoordinator fetches /accessible-resources on first run but discards the URL, and
/// the refresh path never calls that endpoint at all - so re-authenticating alone would not
/// populate it. Keying the cache on cloud id means switching sites invalidates it for free.
/// </summary>
public sealed class JiraSiteResolver : IJiraSiteResolver
{
    private readonly JiraSiteRepository _repo;
    private readonly IAccessibleSiteSource _sites;
    private readonly Func<string?> _activeCloudId;
    private readonly ILogger<JiraSiteResolver> _log;

    public JiraSiteResolver(JiraSiteRepository repo, IAccessibleSiteSource sites,
        Func<string?> activeCloudId, ILogger<JiraSiteResolver> log)
    {
        _repo = repo; _sites = sites; _activeCloudId = activeCloudId; _log = log;
    }

    public async Task<string?> GetSiteUrlAsync(CancellationToken ct)
    {
        var cloudId = _activeCloudId();
        if (string.IsNullOrEmpty(cloudId)) return null;

        var cached = _repo.Get();
        if (cached is not null && cached.CloudId == cloudId) return cached.SiteUrl;

        try
        {
            var resources = await _sites.ListAccessibleAsync(ct).ConfigureAwait(false);
            var match = resources.FirstOrDefault(r => r.Id == cloudId);
            if (match is null)
            {
                _log.LogWarning("Active cloudId {CloudId} is not among accessible sites", cloudId);
                return null;
            }

            _repo.Save(new JiraSite(match.Id, match.Url));
            return match.Url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Returning null keeps {URL} unresolved, which renders literally rather than blank.
            _log.LogWarning(ex, "Could not resolve the Jira site URL");
            return null;
        }
    }
}
