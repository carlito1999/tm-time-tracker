using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class MessageTemplateRendererTests
{
    private static Dictionary<string, string> Vars() => new(StringComparer.Ordinal)
    {
        ["TICKET"] = "SN-296",
        ["SUMMARY"] = "Fix Tolgee warning",
        ["TO"] = "Review",
    };

    [Fact]
    public void Substitutes_known_placeholders()
    {
        MessageTemplateRenderer.Render("{TICKET} -> {TO}", Vars())
            .Should().Be("SN-296 -> Review");
    }

    [Fact]
    public void Leaves_unknown_placeholder_literal()
    {
        MessageTemplateRenderer.Render("{TICKET} {URL}", Vars())
            .Should().Be("SN-296 {URL}");
    }

    // A Jira summary containing braces must never be re-interpreted as a placeholder.
    [Fact]
    public void Does_not_rescan_substituted_text()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SUMMARY"] = "Rename {TICKET} everywhere",
            ["TICKET"] = "SN-296",
        };
        MessageTemplateRenderer.Render("{SUMMARY}", vars)
            .Should().Be("Rename {TICKET} everywhere");
    }

    [Fact]
    public void Template_without_placeholders_is_unchanged()
    {
        MessageTemplateRenderer.Render("plain text", Vars()).Should().Be("plain text");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_template_renders_empty(string? template)
    {
        MessageTemplateRenderer.Render(template, Vars()).Should().Be("");
    }

    [Fact]
    public void Lowercase_placeholders_are_not_substituted()
    {
        MessageTemplateRenderer.Render("{ticket}", Vars()).Should().Be("{ticket}");
    }
}
