using System.Text.Json;

namespace TmTimeTracker.Logic;

/// <summary>Which verification gate rejected a run. Recorded so the toast can name it.</summary>
public enum EstimateGate
{
    Process,
    Schema,
    Sanity
}

public sealed record TicketEstimate(
    int ImplementationMinutes,
    int TestingMinutes,
    int ReviewMinutes,
    string Confidence,
    string Rationale)
{
    public int TotalMinutes => ImplementationMinutes + TestingMinutes + ReviewMinutes;
}

public sealed record EstimateParse(TicketEstimate? Value, EstimateGate? FailedGate, string? Error)
{
    public bool Ok => Value is not null;
}

/// <summary>
/// Turns one `claude -p --output-format json` run into an estimate, or into a named gate
/// failure. Gates 1 (process), 2 (schema) and 3 (sanity) live here; gate 4 is the Jira
/// read-back and belongs to the worker.
///
/// Claude is never asked whether it succeeded. Everything below is derived from the artifacts
/// the run left behind, because a self-report is exactly the thing that cannot be checked.
///
/// The CLI emits a JSON ARRAY of events - not a single object - and the payload sits on the
/// last element whose type is "result". That element carries both a `result` string and an
/// already-parsed `structured_output` object; the object is used, so the payload is decoded
/// once rather than twice.
/// </summary>
public static class EstimateResult
{
    // Two working weeks on a single ticket is not an estimate, it is a malfunction - and it
    // would be written straight onto the ticket, so it is rejected rather than clamped.
    private const int MaxMinutesPerPhase = 20160;

    private static readonly HashSet<string> Confidences =
        new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" };

    // Structurally valid but semantically empty: the schema was satisfied without answering.
    private static readonly HashSet<string> Placeholders =
        new(StringComparer.OrdinalIgnoreCase)
        { "tbd", "n/a", "na", "none", "unknown", "todo", "-", "?" };

    public static EstimateParse Parse(string? stdout, int exitCode)
    {
        if (exitCode != 0)
            return Fail(EstimateGate.Process, $"claude exited with code {exitCode}.");

        if (string.IsNullOrWhiteSpace(stdout))
            return Fail(EstimateGate.Process, "claude produced no output.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            return Fail(EstimateGate.Process, $"claude output was not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var result = FindResultEvent(doc.RootElement);
            if (result is null)
                return Fail(EstimateGate.Process, "claude output contained no result event.");

            var evt = result.Value;

            if (evt.TryGetProperty("is_error", out var isError) &&
                isError.ValueKind == JsonValueKind.True)
                return Fail(EstimateGate.Process, Reason(evt) ?? "claude reported an error.");

            if (evt.TryGetProperty("subtype", out var subtype) &&
                subtype.ValueKind == JsonValueKind.String &&
                !string.Equals(subtype.GetString(), "success", StringComparison.Ordinal))
                return Fail(EstimateGate.Process, $"claude finished as '{subtype.GetString()}'.");

            return FromPayload(evt);
        }
    }

    private static EstimateParse FromPayload(JsonElement evt)
    {
        if (!evt.TryGetProperty("structured_output", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
            return Fail(EstimateGate.Schema, "claude returned no structured output.");

        if (!Int(payload, "implementation_minutes", out var impl, out var err) ||
            !Int(payload, "testing_minutes", out var test, out err) ||
            !Int(payload, "review_minutes", out var review, out err))
            return Fail(EstimateGate.Schema, err!);

        if (!Str(payload, "confidence", out var confidence, out err) ||
            !Str(payload, "rationale", out var rationale, out err))
            return Fail(EstimateGate.Schema, err!);

        return Sanity(new TicketEstimate(impl, test, review, confidence!, rationale!));
    }

    private static EstimateParse Sanity(TicketEstimate e)
    {
        foreach (var (name, minutes) in new[]
                 {
                     ("implementation", e.ImplementationMinutes),
                     ("testing", e.TestingMinutes),
                     ("review", e.ReviewMinutes)
                 })
        {
            if (minutes < 1)
                return Fail(EstimateGate.Sanity, $"{name} estimate was {minutes} minutes.");
            if (minutes > MaxMinutesPerPhase)
                return Fail(EstimateGate.Sanity,
                    $"{name} estimate of {minutes} minutes exceeds the {MaxMinutesPerPhase} minute ceiling.");
        }

        if (!Confidences.Contains(e.Confidence))
            return Fail(EstimateGate.Sanity, $"unknown confidence '{e.Confidence}'.");

        var rationale = e.Rationale.Trim();
        if (rationale.Length < 3 || Placeholders.Contains(rationale))
            return Fail(EstimateGate.Sanity, "the rationale was empty or a placeholder.");

        return new EstimateParse(e, null, null);
    }

    private static JsonElement? FindResultEvent(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
            return IsResult(root) ? root : null;

        if (root.ValueKind != JsonValueKind.Array) return null;

        JsonElement? found = null;
        foreach (var element in root.EnumerateArray())
            if (element.ValueKind == JsonValueKind.Object && IsResult(element))
                found = element;   // last one wins
        return found;
    }

    private static bool IsResult(JsonElement e) =>
        e.TryGetProperty("type", out var t) &&
        t.ValueKind == JsonValueKind.String &&
        string.Equals(t.GetString(), "result", StringComparison.Ordinal);

    private static string? Reason(JsonElement evt) =>
        evt.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.String
            ? $"claude reported an error: {s.GetString()}"
            : null;

    private static bool Int(JsonElement payload, string name, out int value, out string? error)
    {
        value = 0;
        error = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            error = $"'{name}' was missing from the structured output.";
            return false;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = $"'{name}' was not a whole number.";
            return false;
        }
        return true;
    }

    private static bool Str(JsonElement payload, string name, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            error = $"'{name}' was missing from the structured output.";
            return false;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"'{name}' was not a string.";
            return false;
        }
        value = element.GetString() ?? "";
        return true;
    }

    private static EstimateParse Fail(EstimateGate gate, string error) => new(null, gate, error);
}
