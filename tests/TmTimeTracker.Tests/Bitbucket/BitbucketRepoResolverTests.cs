using FluentAssertions;
using TmTimeTracker.Bitbucket;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Bitbucket;

public class BitbucketRepoResolverTests
{
    private sealed class FakeRemotes : IGitRemoteProbe
    {
        private readonly Dictionary<string, string?> _remotes = new(StringComparer.OrdinalIgnoreCase);
        public FakeRemotes Add(string path, string? remote) { _remotes[path] = remote; return this; }
        public string? GetRemoteUrl(string repoPath) =>
            _remotes.TryGetValue(repoPath, out var remote) ? remote : null;
    }

    private static BitbucketRepoResolver Build(
        IGitRemoteProbe remotes, params (string Path, string Project)[] mappings)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        var projects = new RepoProjectRepository(factory);
        foreach (var (path, project) in mappings) projects.Save(path, project, autoMatched: true);

        return new BitbucketRepoResolver(projects, remotes);
    }

    [Fact]
    public void Resolves_the_repo_mapped_to_the_tickets_project()
    {
        var resolver = Build(
            new FakeRemotes().Add(@"C:\projects\sheeponline-new",
                                  "git@bitbucket.org:thecubeee/sheeponline-new.git"),
            (@"C:\projects\sheeponline-new", "SN"));

        resolver.ReposFor("SN-291").Should().ContainSingle()
            .Which.Should().Be(new BitbucketRepo("thecubeee", "sheeponline-new"));
    }

    // Two repos can serve one board, and a ticket's branch may live in either.
    [Fact]
    public void Resolves_every_repo_mapped_to_the_project()
    {
        var resolver = Build(
            new FakeRemotes()
                .Add(@"C:\projects\api", "git@bitbucket.org:thecubeee/api.git")
                .Add(@"C:\projects\web", "https://bitbucket.org/thecubeee/web.git"),
            (@"C:\projects\api", "SN"), (@"C:\projects\web", "SN"));

        resolver.ReposFor("SN-291").Select(r => r.Slug)
            .Should().BeEquivalentTo("api", "web");
    }

    [Fact]
    public void Ignores_repos_mapped_to_another_project()
    {
        var resolver = Build(
            new FakeRemotes()
                .Add(@"C:\projects\sheeponline-new", "git@bitbucket.org:thecubeee/sheeponline-new.git")
                .Add(@"C:\projects\tm-time-tracker", "git@bitbucket.org:thecubeee/tm-time-tracker.git"),
            (@"C:\projects\sheeponline-new", "SN"), (@"C:\projects\tm-time-tracker", "TM"));

        resolver.ReposFor("SN-291").Should().ContainSingle()
            .Which.Slug.Should().Be("sheeponline-new");
    }

    [Fact]
    public void Skips_repos_hosted_somewhere_other_than_bitbucket()
    {
        var resolver = Build(
            new FakeRemotes().Add(@"C:\projects\web", "git@github.com:thecubeee/web.git"),
            (@"C:\projects\web", "SN"));

        resolver.ReposFor("SN-291").Should().BeEmpty();
    }

    [Fact]
    public void Skips_repos_with_no_remote()
    {
        var resolver = Build(
            new FakeRemotes().Add(@"C:\projects\web", null),
            (@"C:\projects\web", "SN"));

        resolver.ReposFor("SN-291").Should().BeEmpty();
    }

    // Two clones of one repository are one Bitbucket repository, and must not be queried twice.
    [Fact]
    public void Collapses_two_clones_of_the_same_repository()
    {
        var resolver = Build(
            new FakeRemotes()
                .Add(@"C:\projects\web", "git@bitbucket.org:thecubeee/web.git")
                .Add(@"C:\projects\web-2", "https://bitbucket.org/thecubeee/web.git"),
            (@"C:\projects\web", "SN"), (@"C:\projects\web-2", "SN"));

        resolver.ReposFor("SN-291").Should().ContainSingle();
    }

    [Fact]
    public void Returns_nothing_for_a_key_that_is_not_a_ticket()
    {
        var resolver = Build(
            new FakeRemotes().Add(@"C:\projects\web", "git@bitbucket.org:thecubeee/web.git"),
            (@"C:\projects\web", "SN"));

        resolver.ReposFor("not-a-ticket").Should().BeEmpty();
    }
}
