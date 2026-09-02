using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class SlackVariablesTests
{
    private static IReadOnlyDictionary<string, string> Build(
        string? summary = "Fix Tolgee warning",
        string? siteUrl = "https://tcubeee.atlassian.net",
        string? prUrl = "https://bitbucket.org/acme/web/pull-requests/360",
        string? prTitle = "SN-296-372: enhance Tolgee caching",
        string? prStatus = "OPEN") =>
        SlackVariables.Build("SN-296", summary, "In Progress", "Review", 137,
            new DateTime(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc), siteUrl,
            prUrl, prTitle, prStatus);

    [Fact]
    public void Populates_every_variable_when_all_data_is_present()
    {
        var v = Build();
        v["TICKET"].Should().Be("SN-296");
        v["PROJECT"].Should().Be("SN");
        v["SUMMARY"].Should().Be("Fix Tolgee warning");
        v["URL"].Should().Be("https://tcubeee.atlassian.net/browse/SN-296");
        v["FROM"].Should().Be("In Progress");
        v["TO"].Should().Be("Review");
        v["MINUTES"].Should().Be("137");
        v["HOURS"].Should().Be("2h 17m");
    }

    [Fact]
    public void Omits_url_when_site_url_is_unknown()
    {
        Build(siteUrl: null).ContainsKey("URL").Should().BeFalse();
    }

    [Fact]
    public void Omits_summary_when_blank()
    {
        Build(summary: "  ").ContainsKey("SUMMARY").Should().BeFalse();
    }

    [Fact]
    public void Populates_pull_request_variables()
    {
        var v = Build();
        v["PR_URL"].Should().Be("https://bitbucket.org/acme/web/pull-requests/360");
        v["PR_TITLE"].Should().Be("SN-296-372: enhance Tolgee caching");
        v["PR_STATUS"].Should().Be("OPEN");
    }

    [Fact]
    public void Omits_pull_request_variables_when_no_pr_is_linked()
    {
        var v = Build(prUrl: null, prTitle: null, prStatus: null);
        v.ContainsKey("PR_URL").Should().BeFalse();
        v.ContainsKey("PR_TITLE").Should().BeFalse();
        v.ContainsKey("PR_STATUS").Should().BeFalse();
    }

    [Fact]
    public void Strips_trailing_slash_from_site_url()
    {
        Build(siteUrl: "https://tcubeee.atlassian.net/")["URL"]
            .Should().Be("https://tcubeee.atlassian.net/browse/SN-296");
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    [InlineData(137, "2h 17m")]
    public void Formats_hours(int minutes, string expected)
    {
        SlackVariables.FormatHours(minutes).Should().Be(expected);
    }

    // Drift guard: the Settings panel renders from Catalog, so a variable added to
    // Build without being catalogued (or vice versa) must fail here.
    [Fact]
    public void Catalog_matches_the_keys_build_can_emit()
    {
        var produced = Build().Keys.ToHashSet();
        var advertised = SlackVariables.Catalog.Select(c => c.Name).ToHashSet();
        advertised.Should().BeEquivalentTo(produced);
    }

    [Fact]
    public void Default_template_only_references_catalogued_variables()
    {
        var rendered = MessageTemplateRenderer.Render(
            SlackVariables.DefaultTemplate, SlackVariables.Sample());
        rendered.Should().NotContain("{");
    }
}
