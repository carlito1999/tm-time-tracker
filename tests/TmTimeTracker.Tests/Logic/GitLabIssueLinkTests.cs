using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// Two URL shapes name the same GitLab issue - /-/issues/377 and /-/work_items/377 - and both
/// turn up in Jira descriptions here. SN-305's description is a bare work_items link.
/// </summary>
public class GitLabIssueLinkTests
{
    [Theory]
    [InlineData("https://gitlab.com/si-bv/stamboekonline/-/work_items/377", "si-bv/stamboekonline", 377L)]
    [InlineData("https://gitlab.com/si-bv/stamboekonline/-/issues/377", "si-bv/stamboekonline", 377L)]
    [InlineData("https://gitlab.com/a/b/c/d/-/issues/12", "a/b/c/d", 12L)]
    [InlineData("http://gitlab.com/a/b/-/issues/1", "a/b", 1L)]
    [InlineData("https://gitlab.com/a/b/-/issues/7/", "a/b", 7L)]
    [InlineData("https://gitlab.com/a/b/-/issues/7#note_9", "a/b", 7L)]
    [InlineData("https://gitlab.com/a/b/-/issues/7?foo=bar", "a/b", 7L)]
    [InlineData("  https://gitlab.com/a/b/-/issues/7  ", "a/b", 7L)]
    public void Parses_project_path_and_iid(string url, string project, long iid)
    {
        var link = GitLabIssueLink.Parse(url);

        link.Should().NotBeNull();
        link!.ProjectPath.Should().Be(project);
        link.Iid.Should().Be(iid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://gitlab.com/si-bv/stamboekonline")]
    [InlineData("https://gitlab.com/a/b/-/merge_requests/5")]
    [InlineData("https://gitlab.com/-/issues/5")]
    [InlineData("https://gitlab.com/a/b/-/issues/notanumber")]
    [InlineData("https://gitlab.com/a/b/-/issues/0")]
    public void Rejects_anything_that_is_not_an_issue_link(string? url)
    {
        GitLabIssueLink.Parse(url).Should().BeNull();
    }

    // Only gitlab.com. A self-hosted instance needs its own base URL in settings, and silently
    // trusting an arbitrary host would send the token somewhere unintended.
    [Theory]
    [InlineData("https://example.com/a/b/-/issues/5")]
    [InlineData("https://gitlab.example.com/a/b/-/issues/5")]
    [InlineData("https://notgitlab.com/a/b/-/issues/5")]
    public void Rejects_hosts_other_than_gitlab_dot_com(string url)
    {
        GitLabIssueLink.Parse(url).Should().BeNull();
    }

    [Fact]
    public void Keeps_the_original_url_so_the_prompt_can_cite_it()
    {
        GitLabIssueLink.Parse("https://gitlab.com/a/b/-/work_items/3")!
            .Url.Should().Be("https://gitlab.com/a/b/-/work_items/3");
    }

    [Fact]
    public void FindAll_keeps_only_issue_links_and_de_duplicates_by_project_and_iid()
    {
        var links = GitLabIssueLink.FindAll(new[]
        {
            "https://gitlab.com/a/b/-/issues/1",
            "https://gitlab.com/a/b/-/work_items/1",   // same issue, other URL shape
            "https://example.com/nope",
            "https://gitlab.com/a/b/-/issues/2"
        });

        links.Should().HaveCount(2);
        links.Select(l => l.Iid).Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    [Fact]
    public void FindAll_treats_the_same_iid_in_different_projects_as_different_issues()
    {
        var links = GitLabIssueLink.FindAll(new[]
        {
            "https://gitlab.com/a/b/-/issues/1",
            "https://gitlab.com/c/d/-/issues/1"
        });

        links.Should().HaveCount(2);
    }

    [Fact]
    public void FindAll_caps_how_many_links_one_ticket_can_pull_in()
    {
        var many = Enumerable.Range(1, 10).Select(i => $"https://gitlab.com/a/b/-/issues/{i}");

        GitLabIssueLink.FindAll(many).Should().HaveCount(GitLabIssueLink.MaxLinks);
    }

    [Fact]
    public void FindAll_returns_empty_for_null()
    {
        GitLabIssueLink.FindAll(null).Should().BeEmpty();
    }
}
