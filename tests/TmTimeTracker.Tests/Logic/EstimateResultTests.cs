using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// The envelope shape here was captured from a real `claude -p --output-format json` run on
/// 2026-09-03: the CLI emits a JSON ARRAY of events, and the payload lives on the final
/// element whose type is "result".
/// </summary>
public class EstimateResultTests
{
    private static string Envelope(string? structuredOutput, bool isError = false,
        string subtype = "success")
    {
        var so = structuredOutput is null ? "null" : structuredOutput;
        return $$"""
        [
          {"type":"system","subtype":"init","session_id":"abc"},
          {"type":"result","subtype":"{{subtype}}","is_error":{{(isError ? "true" : "false")}},
           "total_cost_usd":0.11,"result":"ignored",
           "structured_output":{{so}}}
        ]
        """;
    }

    private const string GoodPayload = """
        {"implementation_minutes":90,"testing_minutes":30,"review_minutes":20,
         "confidence":"medium","rationale":"Touches three files and the poll loop."}
        """;

    [Fact]
    public void Parses_a_successful_run()
    {
        var parsed = EstimateResult.Parse(Envelope(GoodPayload), exitCode: 0);

        parsed.Ok.Should().BeTrue();
        parsed.Value!.ImplementationMinutes.Should().Be(90);
        parsed.Value.TestingMinutes.Should().Be(30);
        parsed.Value.ReviewMinutes.Should().Be(20);
        parsed.Value.Confidence.Should().Be("medium");
        parsed.Value.Rationale.Should().Be("Touches three files and the poll loop.");
    }

    [Fact]
    public void Totals_the_three_phases()
    {
        EstimateResult.Parse(Envelope(GoodPayload), 0).Value!.TotalMinutes.Should().Be(140);
    }

    // --- Gate 1: process -----------------------------------------------------------------

    [Fact]
    public void Fails_the_process_gate_on_a_nonzero_exit_code()
    {
        var parsed = EstimateResult.Parse(Envelope(GoodPayload), exitCode: 1);

        parsed.Ok.Should().BeFalse();
        parsed.FailedGate.Should().Be(EstimateGate.Process);
    }

    [Fact]
    public void Fails_the_process_gate_when_the_cli_reports_an_error()
    {
        EstimateResult.Parse(Envelope(GoodPayload, isError: true), 0)
            .FailedGate.Should().Be(EstimateGate.Process);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""[{"type":"system","subtype":"init"}]""")]
    public void Fails_the_process_gate_when_there_is_no_result_event(string stdout)
    {
        var parsed = EstimateResult.Parse(stdout, exitCode: 0);

        parsed.Ok.Should().BeFalse();
        parsed.FailedGate.Should().Be(EstimateGate.Process);
    }

    // A run killed at the timeout exits non-zero and prints a partial array; it must never be
    // mistaken for an answer.
    [Fact]
    public void Fails_the_process_gate_on_truncated_output()
    {
        EstimateResult.Parse("""[{"type":"result","subty""", exitCode: 143)
            .FailedGate.Should().Be(EstimateGate.Process);
    }

    // --- Gate 2: schema ------------------------------------------------------------------

    [Fact]
    public void Fails_the_schema_gate_when_structured_output_is_absent()
    {
        var parsed = EstimateResult.Parse(Envelope(null), 0);

        parsed.Ok.Should().BeFalse();
        parsed.FailedGate.Should().Be(EstimateGate.Schema);
    }

    [Theory]
    [InlineData("""{"testing_minutes":30,"review_minutes":20,"confidence":"high","rationale":"x"}""")]
    [InlineData("""{"implementation_minutes":90,"review_minutes":20,"confidence":"high","rationale":"x"}""")]
    [InlineData("""{"implementation_minutes":90,"testing_minutes":30,"confidence":"high","rationale":"x"}""")]
    [InlineData("""{"implementation_minutes":90,"testing_minutes":30,"review_minutes":20,"rationale":"x"}""")]
    [InlineData("""{"implementation_minutes":90,"testing_minutes":30,"review_minutes":20,"confidence":"high"}""")]
    public void Fails_the_schema_gate_when_a_required_field_is_missing(string payload)
    {
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Schema);
    }

    [Fact]
    public void Fails_the_schema_gate_when_minutes_are_not_a_number()
    {
        var payload = """
            {"implementation_minutes":"ninety","testing_minutes":30,"review_minutes":20,
             "confidence":"high","rationale":"x"}
            """;
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Schema);
    }

    // --- Gate 3: sanity ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Fails_the_sanity_gate_on_non_positive_minutes(int minutes)
    {
        var payload = $$"""
            {"implementation_minutes":{{minutes}},"testing_minutes":30,"review_minutes":20,
             "confidence":"high","rationale":"x"}
            """;
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Sanity);
    }

    // A field larger than two working weeks is not an estimate, it is a malfunction - and it
    // would be written straight onto the ticket.
    [Fact]
    public void Fails_the_sanity_gate_on_an_absurd_duration()
    {
        var payload = """
            {"implementation_minutes":999999,"testing_minutes":30,"review_minutes":20,
             "confidence":"high","rationale":"x"}
            """;
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Sanity);
    }

    [Fact]
    public void Fails_the_sanity_gate_on_an_unknown_confidence()
    {
        var payload = """
            {"implementation_minutes":90,"testing_minutes":30,"review_minutes":20,
             "confidence":"probably fine","rationale":"x"}
            """;
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Sanity);
    }

    // Structurally valid but semantically empty: the model filled the field to satisfy the
    // schema without actually answering.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TBD")]
    [InlineData("n/a")]
    [InlineData("unknown")]
    public void Fails_the_sanity_gate_on_a_placeholder_rationale(string rationale)
    {
        var payload = $$"""
            {"implementation_minutes":90,"testing_minutes":30,"review_minutes":20,
             "confidence":"high","rationale":"{{rationale}}"}
            """;
        EstimateResult.Parse(Envelope(payload), 0).FailedGate.Should().Be(EstimateGate.Sanity);
    }

    [Fact]
    public void Reports_an_error_message_on_every_failure()
    {
        EstimateResult.Parse(Envelope(null), 0).Error.Should().NotBeNullOrWhiteSpace();
    }
}
