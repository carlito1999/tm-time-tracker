using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

public sealed record OAuthAppConfig(string ClientId, string ClientSecret, string RedirectUri);

public sealed class OAuthAppConfigRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public OAuthAppConfigRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(OAuthAppConfig c)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO oauth_app_config (id, client_id_dpapi, client_secret_dpapi, redirect_uri)
              VALUES (1, @cid, @sec, @ru)
              ON CONFLICT(id) DO UPDATE SET
                client_id_dpapi=excluded.client_id_dpapi,
                client_secret_dpapi=excluded.client_secret_dpapi,
                redirect_uri=excluded.redirect_uri",
            new
            {
                cid = _protector.Protect(c.ClientId),
                sec = _protector.Protect(c.ClientSecret),
                ru = c.RedirectUri
            });
    }

    public OAuthAppConfig? Load()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<AppConfigRow>(
            "SELECT * FROM oauth_app_config WHERE id=1");
        if (row is null) return null;
        return new OAuthAppConfig(
            ClientId: _protector.Unprotect(row.client_id_dpapi),
            ClientSecret: _protector.Unprotect(row.client_secret_dpapi),
            RedirectUri: row.redirect_uri);
    }

    private sealed class AppConfigRow
    {
        public byte[] client_id_dpapi { get; set; } = Array.Empty<byte>();
        public byte[] client_secret_dpapi { get; set; } = Array.Empty<byte>();
        public string redirect_uri { get; set; } = "";
    }
}
