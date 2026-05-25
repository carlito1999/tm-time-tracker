using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class OAuthAppConfigRepositoryTests
{
    private sealed class FakeProtector : ITokenProtector
    {
        public byte[] Protect(string plaintext) =>
            System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] ciphertext) =>
            System.Text.Encoding.UTF8.GetString(ciphertext);
    }

    private static OAuthAppConfigRepository New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return new OAuthAppConfigRepository(ds, new FakeProtector());
    }

    [Fact]
    public void Save_round_trips_encrypted_credentials()
    {
        var repo = New();
        var cfg = new OAuthAppConfig("client-abc", "super-secret", "http://localhost:53682/callback");
        repo.Save(cfg);

        var loaded = repo.Load();
        loaded.Should().NotBeNull();
        loaded!.ClientId.Should().Be("client-abc");
        loaded.ClientSecret.Should().Be("super-secret");
        loaded.RedirectUri.Should().Be("http://localhost:53682/callback");
    }

    [Fact]
    public void Save_overwrites_existing_row()
    {
        var repo = New();
        repo.Save(new OAuthAppConfig("old-id", "old-secret", "http://localhost:53682/callback"));
        repo.Save(new OAuthAppConfig("new-id", "new-secret", "http://localhost:53682/callback"));
        repo.Load()!.ClientId.Should().Be("new-id");
        repo.Load()!.ClientSecret.Should().Be("new-secret");
    }

    [Fact]
    public void Load_returns_null_when_absent()
    {
        New().Load().Should().BeNull();
    }
}
