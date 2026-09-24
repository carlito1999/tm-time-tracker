using FluentAssertions;
using TmTimeTracker.Jev;
using Xunit;

namespace TmTimeTracker.Tests.Jev;

/// <summary>
/// Question sets are kept as files in the API's own wire format, so a set can be edited, diffed
/// and replayed without recompiling - and so the file that ships beside fitted weights is exactly
/// the one the weights were fitted against.
/// </summary>
public class JevQuestionSetTests
{
    [Fact]
    public void Reads_a_noul_with_its_criteria()
    {
        var set = JevQuestionSet.Parse("""
            { "is_bug": { "type": "noul", "instructions": "Is something broken?",
                          "criteria": { "true": "A defect", "false": "A request" } } }
            """);

        set["is_bug"].Should().Be(new NoulQuestion("Is something broken?", "A defect", "A request"));
    }

    [Fact]
    public void Reads_a_noul_without_criteria()
    {
        var set = JevQuestionSet.Parse("""
            { "ui": { "type": "noul", "instructions": "Does it change a screen?" } }
            """);

        set["ui"].Should().Be(new NoulQuestion("Does it change a screen?"));
    }

    [Fact]
    public void Reads_score_levels_lowest_first()
    {
        var set = JevQuestionSet.Parse("""
            { "scope": { "type": "score", "instructions": "How much changes?",
                         "criteria": ["A label", "One file", "A feature"] } }
            """);

        var scope = set["scope"].Should().BeOfType<ScoreQuestion>().Subject;
        scope.Instructions.Should().Be("How much changes?");
        scope.Levels.Should().Equal("A label", "One file", "A feature");
    }

    [Fact]
    public void Reads_choice_options()
    {
        var set = JevQuestionSet.Parse("""
            { "layer": { "type": "choice", "instructions": "Which layer?",
                         "criteria": { "ui": "Screens", "api": "Endpoints" } } }
            """);

        var layer = set["layer"].Should().BeOfType<ChoiceQuestion>().Subject;
        layer.Options.Should().Equal(new Dictionary<string, string> { ["ui"] = "Screens", ["api"] = "Endpoints" });
    }

    [Fact]
    public void Keeps_every_question_in_file_order()
    {
        var set = JevQuestionSet.Parse("""
            { "b": { "type": "noul", "instructions": "B?" },
              "a": { "type": "noul", "instructions": "A?" } }
            """);

        set.Keys.Should().Equal("b", "a");
    }

    [Fact]
    public void Rejects_an_unknown_question_type()
    {
        var act = () => JevQuestionSet.Parse("""
            { "x": { "type": "essay", "instructions": "Write about it" } }
            """);

        act.Should().Throw<FormatException>().WithMessage("*x*essay*");
    }
}
