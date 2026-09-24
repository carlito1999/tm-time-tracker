using System.Text.Json;

namespace TmTimeTracker.Jev;

/// <summary>
/// Reads a set of questions written in the System One wire format - the same JSON the API takes
/// under "questions" - so a set lives in a file that can be edited and replayed without a rebuild.
/// </summary>
public static class JevQuestionSet
{
    public static IReadOnlyDictionary<string, JevQuestion> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var set = new Dictionary<string, JevQuestion>();

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var q = entry.Value;
            var instructions = q.GetProperty("instructions").GetString() ?? "";
            var hasCriteria = q.TryGetProperty("criteria", out var criteria);

            set[entry.Name] = q.GetProperty("type").GetString() switch
            {
                "noul" => new NoulQuestion(
                    instructions,
                    hasCriteria ? Text(criteria, "true") : null,
                    hasCriteria ? Text(criteria, "false") : null),
                "score" => new ScoreQuestion(
                    instructions,
                    criteria.EnumerateArray().Select(l => l.GetString() ?? "").ToList()),
                "choice" => new ChoiceQuestion(
                    instructions,
                    criteria.EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString() ?? "")),
                var other => throw new FormatException(
                    $"Question '{entry.Name}' has unknown type '{other}'; expected noul, score or choice.")
            };
        }

        return set;
    }

    private static string? Text(JsonElement criteria, string name) =>
        criteria.TryGetProperty(name, out var value) ? value.GetString() : null;
}
