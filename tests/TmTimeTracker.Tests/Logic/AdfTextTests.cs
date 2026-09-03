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
