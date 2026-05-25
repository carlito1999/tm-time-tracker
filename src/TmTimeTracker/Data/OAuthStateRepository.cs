using Dapper;

namespace TmTimeTracker.Data;

public sealed record OAuthState(
    string CloudId,
    byte[] AccessTokenDpapi,
    byte[] RefreshTokenDpapi,
    DateTime AccessExpiresAt,
    string Scope);

public sealed class OAuthStateRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public OAuthStateRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(OAuthState s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO oauth_state
                (id, cloud_id, access_token_dpapi, refresh_token_dpapi,
                 access_expires_at, scope)
              VALUES (1, @c, @a, @r, @e, @sc)
              ON CONFLICT(id) DO UPDATE SET
                cloud_id=excluded.cloud_id,
                access_token_dpapi=excluded.access_token_dpapi,
                refresh_token_dpapi=excluded.refresh_token_dpapi,
                access_expires_at=excluded.access_expires_at,
                scope=excluded.scope",
            new
            {
                c = s.CloudId,
                a = s.AccessTokenDpapi,
                r = s.RefreshTokenDpapi,
                e = s.AccessExpiresAt.ToString("O"),
                sc = s.Scope
            });
    }

    public OAuthState? Load()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<OAuthRow>("SELECT * FROM oauth_state WHERE id=1");
        if (row is null) return null;
        return new OAuthState(
            row.cloud_id,
            row.access_token_dpapi,
            row.refresh_token_dpapi,
            DateTime.Parse(row.access_expires_at, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            row.scope);
    }

    private sealed class OAuthRow
    {
        public string cloud_id { get; set; } = "";
        public byte[] access_token_dpapi { get; set; } = Array.Empty<byte>();
        public byte[] refresh_token_dpapi { get; set; } = Array.Empty<byte>();
        public string access_expires_at { get; set; } = "";
        public string scope { get; set; } = "";
    }
}
