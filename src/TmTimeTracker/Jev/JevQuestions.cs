using System.Text.Json.Nodes;

namespace TmTimeTracker.Jev;

/// <summary>
/// One typed question in a System One request. Jev never answers in prose: each question type
/// has exactly one answer shape, which is the point of using it over a text model.
/// </summary>
public abstract record JevQuestion(string Instructions)
{
    internal abstract JsonObject ToJson();
}

/// <summary>A yes/no question. The answer is the probability of yes.</summary>
public sealed record NoulQuestion(
    string Instructions, string? WhenTrue = null, string? WhenFalse = null)
    : JevQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = "noul", ["instructions"] = Instructions };

        // Omitted rather than sent as null: the documented examples leave criteria out entirely
        // when the question speaks for itself.
        if (WhenTrue is not null || WhenFalse is not null)
        {
            var criteria = new JsonObject();
            if (WhenTrue is not null) criteria["true"] = WhenTrue;
            if (WhenFalse is not null) criteria["false"] = WhenFalse;
            json["criteria"] = criteria;
        }

        return json;
    }
}

/// <summary>One of a fixed, unordered set of options (at most 255), keyed by option id.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Options)
    : JevQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var criteria = new JsonObject();
        foreach (var (id, description) in Options) criteria[id] = description;
        return new JsonObject
        {
            ["type"] = "choice", ["instructions"] = Instructions, ["criteria"] = criteria
        };
    }
}

/// <summary>A position on an ordered scale of 2 to 10 levels, lowest first.</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels)
    : JevQuestion(Instructions)
{
    internal override JsonObject ToJson() => new()
    {
        ["type"] = "score",
        ["instructions"] = Instructions,
        ["criteria"] = new JsonArray(Levels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
    };
}
