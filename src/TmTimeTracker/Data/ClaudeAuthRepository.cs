using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

/// <summary>
/// An optional Claude Code OAuth token from `claude setup-token`, encrypted with the same DPAPI
/// protector as every other credential.
///
/// Absent is the normal case, and it is not an error: the estimator then inherits the machine's
/// own Claude Code login. The token exists for the headless case - the daemon starts from
/// HKCU\Run, where an expired interactive login would otherwise fail silently with nowhere in
/// the UI to notice or fix it.
/// </summary>
public sealed class ClaudeAuthRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public ClaudeAuthRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO claude_auth (id, token_dpapi) VALUES (1, @t)
              ON CONFLICT(id) DO UPDATE SET token_dpapi = excluded.token_dpapi",
            new { t = _protector.Protect(token) });
    }

    public string? Get()
    {
        using var conn = _factory.Open();
        var blob = conn.QueryFirstOrDefault<byte[]>(
            "SELECT token_dpapi FROM claude_auth WHERE id = 1");
        return blob is null ? null : _protector.Unprotect(blob);
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM claude_auth WHERE id = 1");
    }
}
