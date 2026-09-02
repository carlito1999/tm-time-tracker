using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class MessageTemplateRenderer
{
    private static readonly Regex Placeholder = new(@"\{(?<name>[A-Z][A-Z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Substitutes {NAME} placeholders from <paramref name="variables"/>. A placeholder with no
    /// matching entry is left literal, so a typo is visible rather than silently blanking text.
    /// Replacement output is never re-scanned, so a value containing braces cannot inject.
    /// </summary>
    public static string Render(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return Placeholder.Replace(template, match =>
            variables.TryGetValue(match.Groups["name"].Value, out var value)
                ? value
                : match.Value);
    }
}
