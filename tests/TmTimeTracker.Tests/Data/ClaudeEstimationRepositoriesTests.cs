using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class RepoProjectRepositoryTests
{
    private static RepoProjectRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new RepoProjectRepository(factory);
    }

    [Fact]
    public void Finds_nothing_for_an_unmapped_repo()
    {
        Build().Find(@"C:\projects\training-manager").Should().BeNull();
    }

    [Fact]
    public void Round_trips_a_mapping()
    {
        var repo = Build();

        repo.Save(@"C:\projects\training-manager", "TM", autoMatched: true);

        repo.Find(@"C:\projects\training-manager").Should().Be("TM");
    }

    // Tracked repo paths are stored as the user picked them, so a lookup must not depend on
    // the casing that reached it - Windows paths are case-insensitive.
    [Fact]
    public void Matches_a_path_regardless_of_case()
    {
        var repo = Build();
        repo.Save(@"C:\projects\training-manager", "TM", autoMatched: true);

        repo.Find(@"c:\PROJECTS\Training-Manager").Should().Be("TM");
    }

    // A user correction in the Repositories tab has to win over the earlier auto-match.
    [Fact]
    public void Overwrites_an_earlier_mapping()
    {
        var repo = Build();
        repo.Save(@"C:\repo", "TM", autoMatched: true);

        repo.Save(@"C:\repo", "SN", autoMatched: false);

        repo.Find(@"C:\repo").Should().Be("SN");
        repo.GetAll().Single().AutoMatched.Should().BeFalse();
    }

    [Fact]
    public void Lists_every_mapping()
    {
        var repo = Build();
        repo.Save(@"C:\a", "TM", autoMatched: true);
        repo.Save(@"C:\b", "SN", autoMatched: true);

        repo.GetAll().Select(m => m.ProjectKey).Should().BeEquivalentTo("TM", "SN");
    }
}

public class RepoEstimationRepositoryTests
{
    private static RepoEstimationRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new RepoEstimationRepository(factory);
    }

    // Every repo tracked before this switch existed has no row, and must keep estimating.
    [Fact]
    public void Treats_a_repo_with_no_row_as_enabled()
    {
        Build().IsEnabled(@"C:\projects\training-manager").Should().BeTrue();
    }

    [Fact]
    public void Disables_a_repo()
    {
        var repo = Build();

        repo.SetEnabled(@"C:\projects\training-manager", false);

        repo.IsEnabled(@"C:\projects\training-manager").Should().BeFalse();
    }

    [Fact]
    public void Re_enables_a_disabled_repo()
    {
        var repo = Build();
        repo.SetEnabled(@"C:\repo", false);

        repo.SetEnabled(@"C:\repo", true);

        repo.IsEnabled(@"C:\repo").Should().BeTrue();
    }

    // The checkbox can fire for a repo that is already in the state it asks for.
    [Fact]
    public void Disabling_twice_is_harmless()
    {
        var repo = Build();
        repo.SetEnabled(@"C:\repo", false);

        repo.SetEnabled(@"C:\repo", false);

        repo.IsEnabled(@"C:\repo").Should().BeFalse();
    }

    [Fact]
    public void Matches_a_path_regardless_of_case()
    {
        var repo = Build();
        repo.SetEnabled(@"C:\projects\training-manager", false);

        repo.IsEnabled(@"c:\PROJECTS\Training-Manager").Should().BeFalse();
    }

    // Removing a repo and adding it back should not silently resurrect an old opt-out.
    [Fact]
    public void Removing_a_repo_forgets_its_opt_out()
    {
        var repo = Build();
        repo.SetEnabled(@"C:\repo", false);

        repo.Remove(@"C:\repo");

        repo.IsEnabled(@"C:\repo").Should().BeTrue();
    }
}

public class ClaudeAuthRepositoryTests
{
    private sealed class PassthroughProtector : ITokenProtector
    {
        public byte[] Protect(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    }

    private static ClaudeAuthRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new ClaudeAuthRepository(factory, new PassthroughProtector());
    }

    // No token stored is the normal case: the daemon then inherits the machine's Claude Code
    // login rather than failing.
    [Fact]
    public void Has_no_token_by_default()
    {
        Build().Get().Should().BeNull();
    }

    [Fact]
    public void Round_trips_a_token()
    {
        var repo = Build();

        repo.Save("sk-ant-oat-example");

        repo.Get().Should().Be("sk-ant-oat-example");
    }

    [Fact]
    public void Replaces_an_existing_token()
    {
        var repo = Build();
        repo.Save("first");

        repo.Save("second");

        repo.Get().Should().Be("second");
    }

    [Fact]
    public void Clearing_falls_back_to_the_ambient_login()
    {
        var repo = Build();
        repo.Save("token");

        repo.Clear();

        repo.Get().Should().BeNull();
    }
}
