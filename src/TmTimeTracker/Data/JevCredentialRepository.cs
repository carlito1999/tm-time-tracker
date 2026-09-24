using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

/// <summary>A Jev key and which provider issued it - see <c>TmTimeTracker.Jev.JevProvider</c>.</summary>
public sealed record JevCredential(string Provider, string Token);

/// <summary>
/// The key Jev calls authenticate with, encrypted with the same DPAPI protector as every other
/// credential.
///
/// The provider is stored with the key because an OpenRouter key and a TypeSafe key are not
/// interchangeable: each is only accepted by its own endpoint. Absent is the normal case - nothing
/// that uses Jev is allowed to require it.
/// </summary>
public sealed class JevCredentialRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public JevCredentialRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(string provider, string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO jev_credential (id, provider, token_dpapi) VALUES (1, @p, @t)
              ON CONFLICT(id) DO UPDATE SET provider = excluded.provider,
                                            token_dpapi = excluded.token_dpapi",
            new { p = provider, t = _protector.Protect(token) });
    }

    public JevCredential? Get()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT provider, token_dpapi FROM jev_credential WHERE id = 1");
        return row is null ? null : new JevCredential(row.provider, _protector.Unprotect(row.token_dpapi));
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM jev_credential WHERE id = 1");
    }

    private sealed class Row
    {
        public string provider { get; set; } = "";
        public byte[] token_dpapi { get; set; } = Array.Empty<byte>();
    }
}
