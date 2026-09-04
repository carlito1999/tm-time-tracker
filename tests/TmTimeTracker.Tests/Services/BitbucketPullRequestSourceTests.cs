using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Bitbucket;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class BitbucketPullRequestSourceTests
{
    private const string Ticket = "SN-291";
    private const string Branch = "SN-291-350-individual-breeders";

    private sealed class FakeRemotes : IGitRemoteProbe
    {
        public string? GetRemoteUrl(string repoPath) =>
            $"git@bitbucket.org:thecubeee/{Path.GetFileName(repoPath)}.git";
    }

    private sealed class FakeBitbucket : IBitbucketPullRequestSource
    {
        private readonly Dictionary<string, PullRequestCandidate[]> _byRepo =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Queried { get; } = new();

        public FakeBitbucket For(string slug, params PullRequestCandidate[] prs)
        {
            _byRepo[slug] = prs;
            return this;
        }

        public Task<IReadOnlyList<PullRequestCandidate>> GetPullRequestsAsync(
            BitbucketRepo repo, string ticketKey, CancellationToken ct)
        {
            Queried.Add(repo.Slug);
            return Task.FromResult<IReadOnlyList<PullRequestCandidate>>(
                _byRepo.TryGetValue(repo.Slug, out var prs) ? prs : Array.Empty<PullRequestCandidate>());
        }
    }

    private sealed class FakeFallback : IPullRequestSource
    {
        private readonly DevInfoSnapshot _snapshot;
        public FakeFallback(DevInfoSnapshot snapshot) => _snapshot = snapshot;
        public int Calls { get; private set; }

        public Task<DevInfoSnapshot> GetSnapshotAsync(string? issueId, string ticketKey, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_snapshot);
        }
    }

    private static PullRequestCandidate Pr(string id, string branch = Branch, string state = "OPEN") =>
        new(id, $"PR {id}", state,
            $"https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/{id}",
            "sheeponline-new", "2026-09-04T05:04:17.554000+00:00", branch);

    private static BitbucketPullRequestSource Build(
        FakeBitbucket bitbucket, FakeFallback fallback, params string[] repoPaths)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        var projects = new RepoProjectRepository(factory);
        foreach (var path in repoPaths) projects.Save(path, "SN", autoMatched: true);

        return new BitbucketPullRequestSource(
            bitbucket,
            new BitbucketRepoResolver(projects, new FakeRemotes()),
            fallback,
            NullLogger<BitbucketPullRequestSource>.Instance);
    }

    // The reported bug: dev-status still had only the superseded 353 fifteen seconds after 367 was
    // created. Bitbucket knew about 367 immediately, so its answer has to win.
    [Fact]
    public async Task Prefers_bitbucket_over_the_dev_status_mirror()
    {
        var fallback = new FakeFallback(new DevInfoSnapshot(
            new PullRequestInfo("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/353",
                                "PR 353", "OPEN"), null));
        var bitbucket = new FakeBitbucket().For("sheeponline-new", Pr("353"), Pr("367"));

        var result = await Build(bitbucket, fallback, @"C:\projects\sheeponline-new")
            .GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        result.PullRequest!.Url.Should().EndWith("/pull-requests/367");
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Falls_back_to_dev_status_when_bitbucket_reports_nothing()
    {
        var fallback = new FakeFallback(new DevInfoSnapshot(
            new PullRequestInfo("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/353",
                                "PR 353", "OPEN"), null));

        var result = await Build(new FakeBitbucket(), fallback, @"C:\projects\sheeponline-new")
            .GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        result.PullRequest!.Url.Should().EndWith("/pull-requests/353");
        fallback.Calls.Should().Be(1);
    }

    // The overdue warning is measured from the branch's last commit, and only dev-status reports
    // it - so the fallback still has to run when there is no pull request to announce.
    [Fact]
    public async Task Reports_the_last_commit_time_from_dev_status_when_no_pull_request_exists()
    {
        var lastCommit = new DateTimeOffset(2026, 9, 3, 13, 7, 25, TimeSpan.Zero);
        var fallback = new FakeFallback(new DevInfoSnapshot(null, lastCommit));

        var result = await Build(new FakeBitbucket(), fallback, @"C:\projects\sheeponline-new")
            .GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
        result.LastCommitUtc.Should().Be(lastCommit);
    }

    [Fact]
    public async Task Queries_every_repo_mapped_to_the_project()
    {
        var bitbucket = new FakeBitbucket().For("web", Pr("367"));

        await Build(bitbucket, new FakeFallback(DevInfoSnapshot.Empty),
                    @"C:\projects\api", @"C:\projects\web")
            .GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        bitbucket.Queried.Should().BeEquivalentTo("api", "web");
    }

    [Fact]
    public async Task Falls_back_without_touching_bitbucket_when_no_repo_is_mapped()
    {
        var bitbucket = new FakeBitbucket();
        var fallback = new FakeFallback(DevInfoSnapshot.Empty);

        await Build(bitbucket, fallback).GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        bitbucket.Queried.Should().BeEmpty();
        fallback.Calls.Should().Be(1);
    }

    // Bitbucket is queried per branch, but the client does not filter - so a pull request from
    // another ticket must still be rejected here rather than announced.
    [Fact]
    public async Task Does_not_announce_another_tickets_pull_request_from_bitbucket()
    {
        var bitbucket = new FakeBitbucket().For(
            "sheeponline-new", Pr("999", branch: "SN-246-273-notifs-&-email"));
        var fallback = new FakeFallback(DevInfoSnapshot.Empty);

        var result = await Build(bitbucket, fallback, @"C:\projects\sheeponline-new")
            .GetSnapshotAsync("28611", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
    }
}
