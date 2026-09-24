namespace TmTimeTracker.Jev;

/// <summary>
/// Where a Jev request goes and what the model is called there.
///
/// OpenRouter forwards TypeSafe's System One body unchanged, so the two providers differ in
/// nothing else. The model is pinned to a version rather than jev-latest on both: an estimator
/// fitted on Jev's answers is only valid for the model that gave them, and a floating alias would
/// let it go stale silently the day the alias moves.
/// </summary>
public sealed record JevProvider(
    string Id, string DisplayName, string BaseUrl, string Model, string KeyPageUrl)
{
    public const string Path = "/v1/systemone";

    public static readonly JevProvider OpenRouter = new(
        "openrouter", "OpenRouter", "https://openrouter.ai/api", "typesafe/jev-1.13",
        "https://openrouter.ai/settings/keys");

    public static readonly JevProvider TypeSafe = new(
        "typesafe", "TypeSafe (direct)", "https://api.typesafe.ai", "jev-1.13.0",
        "https://console.typesafe.ai/keys");

    /// <summary>OpenRouter first: it is the default the settings page offers.</summary>
    public static readonly IReadOnlyList<JevProvider> All = new[] { OpenRouter, TypeSafe };

    public string Endpoint => BaseUrl + Path;

    public static JevProvider? FromId(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
