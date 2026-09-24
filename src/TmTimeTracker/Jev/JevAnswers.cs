namespace TmTimeTracker.Jev;

public abstract record JevAnswer;

/// <summary>Probability, 0 to 1, that the answer is yes.</summary>
public sealed record NoulAnswer(double Probability) : JevAnswer;

/// <summary>
/// The most probable option, the full distribution, and a confidence that measures how
/// concentrated that distribution is - not how likely the pick is to be right.
/// </summary>
public sealed record ChoiceAnswer(
    string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : JevAnswer;

/// <summary>
/// Score is the probability-weighted level index, so it runs from 0 to Levels - 1 rather than 0
/// to 1. Probabilities is indexed by level, lowest first.
/// </summary>
public sealed record ScoreAnswer(
    double Score, double Confidence, IReadOnlyList<double> Probabilities) : JevAnswer;

/// <summary>CostUsd is only reported by OpenRouter; TypeSafe's own endpoint leaves it out.</summary>
public sealed record JevUsage(int InputTokens, int OutputTokens, decimal? CostUsd);

public sealed record JevResult(
    string Model, IReadOnlyDictionary<string, JevAnswer> Answers, JevUsage Usage)
{
    public NoulAnswer Noul(string id) => Get<NoulAnswer>(id);
    public ChoiceAnswer Choice(string id) => Get<ChoiceAnswer>(id);
    public ScoreAnswer Score(string id) => Get<ScoreAnswer>(id);

    private T Get<T>(string id) where T : JevAnswer =>
        Answers.TryGetValue(id, out var answer) && answer is T typed
            ? typed
            : throw new JevException(
                $"Jev returned no {typeof(T).Name.Replace("Answer", "").ToLowerInvariant()} "
                + $"answer for '{id}'.");
}

/// <summary>
/// Every way a Jev call can fail. StatusCode is null when no request was made at all - no key
/// saved - or when the response could not be read.
/// </summary>
public sealed class JevException : Exception
{
    public JevException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

    public bool IsAuthFailure => StatusCode is 401 or 403;
}
