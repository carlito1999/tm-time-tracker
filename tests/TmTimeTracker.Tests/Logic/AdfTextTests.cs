using System.Text.Json;
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// Jira's v3 API returns a description as Atlassian Document Format - a nested JSON tree, not
/// a string. The estimation prompt needs readable text, so the tree is flattened here.
/// </summary>
public class AdfTextTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Returns_empty_for_a_missing_description()
    {
        AdfText.Flatten(null).Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_for_an_empty_document()
    {
        AdfText.Flatten(Doc("""{"type":"doc","version":1,"content":[]}""")).Should().BeEmpty();
    }

    // Jira splits a single sentence across several text nodes when parts of it are styled, so
    // joining with a separator would corrupt ordinary prose.
    [Fact]
    public void Joins_text_nodes_in_a_paragraph_without_adding_separators()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"Retry the poll "},
              {"type":"text","text":"twice","marks":[{"type":"strong"}]},
              {"type":"text","text":" before giving up."}]}]}
            """);

        AdfText.Flatten(adf).Should().Be("Retry the poll twice before giving up.");
    }

    [Fact]
    public void Separates_paragraphs_with_a_line_break()
    {
        var adf = Doc("""
            {"type":"doc","content":[
              {"type":"paragraph","content":[{"type":"text","text":"First."}]},
              {"type":"paragraph","content":[{"type":"text","text":"Second."}]}]}
            """);

        AdfText.Flatten(adf).Should().Be("First.\nSecond.");
    }

    [Fact]
    public void Reaches_text_nested_inside_lists()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"bulletList","content":[
              {"type":"listItem","content":[{"type":"paragraph","content":[
                {"type":"text","text":"item one"}]}]},
              {"type":"listItem","content":[{"type":"paragraph","content":[
                {"type":"text","text":"item two"}]}]}]}]}
            """);

        AdfText.Flatten(adf).Should().Contain("item one").And.Contain("item two");
    }

    [Fact]
    public void Treats_a_hard_break_as_a_line_break()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"one"},{"type":"hardBreak"},{"type":"text","text":"two"}]}]}
            """);

        AdfText.Flatten(adf).Should().Be("one\ntwo");
    }

    // Some Jira configurations still return a plain string here.
    [Fact]
    public void Accepts_a_plain_string_description()
    {
        AdfText.Flatten(Doc("\"just text\"")).Should().Be("just text");
    }

    [Fact]
    public void Returns_empty_for_a_json_null()
    {
        AdfText.Flatten(Doc("null")).Should().BeEmpty();
    }

    // An unknown node type must not swallow the text underneath it - ADF gains node types over
    // time and the description still has to survive.
    [Fact]
    public void Keeps_text_under_an_unrecognised_node_type()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"someFutureNode","content":[
              {"type":"text","text":"still readable"}]}]}
            """);

        AdfText.Flatten(adf).Should().Contain("still readable");
    }

    [Fact]
    public void Does_not_leave_leading_or_trailing_whitespace()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"  padded  "}]}]}
            """);

        AdfText.Flatten(adf).Should().Be("padded");
    }
}

/// <summary>
/// Flattening keeps only visible text, which loses the two shapes a link actually arrives in:
/// an href on a text node's link mark, and a card node that carries the URL in attrs and has no
/// text at all. Jira turns a pasted URL into an inlineCard by default, so the card case is the
/// common one rather than the exotic one.
/// </summary>
public class AdfTextUrlsTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Finds_a_url_in_an_inline_card_which_has_no_text()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"inlineCard","attrs":{"url":"https://gitlab.com/si-bv/stamboekonline/-/work_items/377"}}]}]}
            """);

        AdfText.Urls(adf).Should()
            .ContainSingle().Which.Should()
            .Be("https://gitlab.com/si-bv/stamboekonline/-/work_items/377");
    }

    [Fact]
    public void Finds_the_href_of_a_link_mark_even_when_the_display_text_differs()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"see the ticket","marks":[
                {"type":"link","attrs":{"href":"https://gitlab.com/a/b/-/issues/12"}}]}]}]}
            """);

        AdfText.Urls(adf).Should().ContainSingle().Which.Should()
            .Be("https://gitlab.com/a/b/-/issues/12");
    }

    [Fact]
    public void Finds_a_bare_url_written_as_plain_text()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"repro: https://gitlab.com/a/b/-/issues/9 thanks"}]}]}
            """);

        AdfText.Urls(adf).Should().Contain("https://gitlab.com/a/b/-/issues/9");
    }

    [Fact]
    public void Also_reads_block_and_embed_cards()
    {
        var adf = Doc("""
            {"type":"doc","content":[
              {"type":"blockCard","attrs":{"url":"https://gitlab.com/a/b/-/issues/1"}},
              {"type":"embedCard","attrs":{"url":"https://gitlab.com/a/b/-/issues/2"}}]}
            """);

        AdfText.Urls(adf).Should().BeEquivalentTo(new[]
        {
            "https://gitlab.com/a/b/-/issues/1",
            "https://gitlab.com/a/b/-/issues/2"
        });
    }

    [Fact]
    public void Returns_each_url_once_even_when_repeated()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"inlineCard","attrs":{"url":"https://gitlab.com/a/b/-/issues/5"}},
              {"type":"text","text":"https://gitlab.com/a/b/-/issues/5"}]}]}
            """);

        AdfText.Urls(adf).Should().HaveCount(1);
    }

    [Fact]
    public void Drops_the_full_stop_a_sentence_ends_a_link_with()
    {
        var adf = Doc("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"see https://gitlab.com/a/b/-/issues/3."}]}]}
            """);

        AdfText.Urls(adf).Should().ContainSingle().Which.Should()
            .Be("https://gitlab.com/a/b/-/issues/3");
    }

    [Fact]
    public void Returns_empty_for_a_null_description()
    {
        AdfText.Urls(null).Should().BeEmpty();
    }
}
