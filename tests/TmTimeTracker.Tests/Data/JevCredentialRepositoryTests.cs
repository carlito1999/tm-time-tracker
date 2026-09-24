using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class JevCredentialRepositoryTests
{
    // Reverses the bytes, so a value read straight out of the table without going through
    // Unprotect would come back scrambled rather than passing by accident.
    private sealed class ReversingProtector : ITokenProtector
    {
        public byte[] Protect(string value) =>
            System.Text.Encoding.UTF8.GetBytes(value).Reverse().ToArray();
        public string Unprotect(byte[] value) =>
            System.Text.Encoding.UTF8.GetString(value.Reverse().ToArray());
    }

    private static (JevCredentialRepository Repo, ISqliteConnectionFactory Factory) Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return (new JevCredentialRepository(factory, new ReversingProtector()), factory);
    }

    // No key is the normal case: estimation then runs exactly as it did before Jev existed.
    [Fact]
    public void Has_no_credential_by_default()
    {
        Build().Repo.Get().Should().BeNull();
    }

    [Fact]
    public void Round_trips_the_provider_and_the_key()
    {
        var (repo, _) = Build();

        repo.Save("openrouter", "sk-or-v1-example");

        repo.Get().Should().Be(new JevCredential("openrouter", "sk-or-v1-example"));
    }

    [Fact]
    public void Replaces_an_existing_credential()
    {
        var (repo, _) = Build();
        repo.Save("openrouter", "first");

        repo.Save("typesafe", "second");

        repo.Get().Should().Be(new JevCredential("typesafe", "second"));
    }

    [Fact]
    public void Clearing_removes_the_credential()
    {
        var (repo, _) = Build();
        repo.Save("openrouter", "sk-or-v1-example");

        repo.Clear();

        repo.Get().Should().BeNull();
    }

    [Fact]
    public void The_key_is_stored_through_the_protector_never_as_plain_text()
    {
        var (repo, factory) = Build();

        repo.Save("openrouter", "sk-or-v1-example");

        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT token_dpapi FROM jev_credential WHERE id = 1";
        var blob = (byte[])cmd.ExecuteScalar()!;
        System.Text.Encoding.UTF8.GetString(blob).Should().NotContain("sk-or-v1-example");
    }
}
