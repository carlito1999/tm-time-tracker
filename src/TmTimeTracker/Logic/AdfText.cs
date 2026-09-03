using System.Text;
using System.Text.Json;

namespace TmTimeTracker.Logic;

/// <summary>
/// Flattens an Atlassian Document Format description to plain text for the estimation prompt.
///
/// Jira's v3 API returns a description as a nested JSON tree rather than a string. Text is
/// collected verbatim without separators, because Jira splits a single styled sentence across
/// several text nodes and joining those with a space corrupts ordinary prose.
///
/// Unrecognised node types are descended into rather than skipped: ADF gains node types over
/// time, and a description must not silently lose its content because of one.
/// </summary>
public static class AdfText
{
    // Node types that end a line. Anything else is treated as inline or as a pure container.
    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "paragraph", "heading", "listItem", "blockquote", "codeBlock",
        "panel", "rule", "tableRow", "mediaSingle", "taskItem"
    };

    // ADF is user-supplied and arrives over the network; a pathological tree must not take the
    // daemon down with a stack overflow.
    private const int MaxDepth = 50;

    public static string Flatten(JsonElement? node)
    {
        if (node is null) return "";

        var root = node.Value;
        if (root.ValueKind == JsonValueKind.String) return (root.GetString() ?? "").Trim();
        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return "";

        var sb = new StringBuilder();
        Walk(root, sb, depth: 0);
        return Collapse(sb.ToString());
    }

    private static void Walk(JsonElement node, StringBuilder sb, int depth)
    {
        if (depth > MaxDepth) return;

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) Walk(child, sb, depth + 1);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object) return;

        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        if (type == "text")
        {
            if (node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
            return;
        }

        if (type == "hardBreak")
        {
            sb.Append('\n');
            return;
        }

        if (node.TryGetProperty("content", out var content))
            Walk(content, sb, depth + 1);

        if (type is not null && Blocks.Contains(type)) sb.Append('\n');
    }

    private static string Collapse(string value)
    {
        var sb = new StringBuilder(value.Length);
        var consecutiveNewlines = 0;

        foreach (var c in value)
        {
            if (c == '\n')
            {
                consecutiveNewlines++;
                if (consecutiveNewlines > 2) continue;
            }
            else
            {
                consecutiveNewlines = 0;
            }
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
