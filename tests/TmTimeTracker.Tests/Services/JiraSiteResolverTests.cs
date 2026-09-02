using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraSiteResolverTests
{
    private static JiraSiteRepository NewRepo()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new JiraSiteRepository(factory);
    }

    private static Mock<IAccessibleSiteSource> Sites(params AtlassianResource[] resources)
    {
        var mock = new Mock<IAccessibleSiteSource>();
        mock.Setup(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);
        return mock;
    }

    private static AtlassianResource Site(string id, string url) =>
        new(id, "site", url, Array.Empty<string>());

    private static JiraSiteResolver Resolver(
        JiraSiteRepository repo, IAccessibleSiteSource sites, string? cloudId) =>
        new(repo, sites, () => cloudId, NullLogger<JiraSiteResolver>.Instance);

    [Fact]
    public async Task Fetches_and_caches_the_url_for_the_active_cloud_id()
    {
        var repo = NewRepo();
        var sites = Sites(Site("cloud-1", "https://tcubeee.atlassian.net"));

        var url = await Resolver(repo, sites.Object, "cloud-1").GetSiteUrlAsync(CancellationToken.None);

        url.Should().Be("https://tcubeee.atlassian.net");
        repo.Get()!.SiteUrl.Should().Be("https://tcubeee.atlassian.net");
    }

    [Fact]
    public async Task Second_call_uses_the_cache_and_does_not_refetch()
    {
        var repo = NewRepo();
        var sites = Sites(Site("cloud-1", "https://tcubeee.atlassian.net"));
        var resolver = Resolver(repo, sites.Object, "cloud-1");

        await resolver.GetSiteUrlAsync(CancellationToken.None);
        await resolver.GetSiteUrlAsync(CancellationToken.None);

        sites.Verify(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_changed_cloud_id_invalidates_the_cache()
    {
        var repo = NewRepo();
        repo.Save(new JiraSite("cloud-OLD", "https://old.atlassian.net"));
        var sites = Sites(Site("cloud-NEW", "https://new.atlassian.net"));

        var url = await Resolver(repo, sites.Object, "cloud-NEW").GetSiteUrlAsync(CancellationToken.None);

        url.Should().Be("https://new.atlassian.net");
    }

    [Fact]
    public async Task Returns_null_when_the_active_cloud_id_is_not_accessible()
    {
        var repo = NewRepo();
        var sites = Sites(Site("other", "https://other.atlassian.net"));

        var url = await Resolver(repo, sites.Object, "cloud-1").GetSiteUrlAsync(CancellationToken.None);

        url.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_not_authenticated()
    {
        var repo = NewRepo();
        var sites = Sites();

        var url = await Resolver(repo, sites.Object, null).GetSiteUrlAsync(CancellationToken.None);

        url.Should().BeNull();
        sites.Verify(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Offline must degrade to a literal {URL}, never take down the notification.
    [Fact]
    public async Task Returns_null_and_does_not_throw_when_the_lookup_fails()
    {
        var repo = NewRepo();
        var sites = new Mock<IAccessibleSiteSource>();
        sites.Setup(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()))
             .ThrowsAsync(new HttpRequestException("offline"));

        var url = await Resolver(repo, sites.Object, "cloud-1").GetSiteUrlAsync(CancellationToken.None);

        url.Should().BeNull();
    }
}
