using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TmTimeTracker.Jev;

/// <summary>What the estimator made of one Jev answer.</summary>
/// <param name="RawMinutes">The model's prediction before any rounding.</param>
public sealed record JevEstimate(
    double ExpectedMinutes, double MiddleMinutes, double Confidence, double RawMinutes)
{
    public int Minutes => Math.Max(1, (int)Math.Round(RawMinutes));
}

/// <summary>
/// Turns Jev's answer to "how long will this ticket take?" into minutes.
///
/// Jev picks from 15-minute steps and spreads its probability across several of them. Its raw
/// pick is not a usable figure on its own: across 140 finished tickets it ranked tickets well but
/// missed by more than a flat median guess, running high on small tickets. So the answer goes
/// through a small model fitted on logged time - log minutes as a weighted sum of the log average,
/// the log middle, and the confidence of Jev's distribution - which cut the average miss from
/// 56 minutes (median guess) to 48.
///
/// The question and the weights ship together in one embedded file written by
/// tools/jev/fit_estimator.py, so they cannot drift apart. The features read here must match
/// that script's exactly; its stored check case is what the tests hold this class to.
/// </summary>
public sealed class JevEstimator
{
    private const string ResourceName = "TmTimeTracker.Jev.jev-estimator.json";
    private static readonly string[] ExpectedFeatures =
        ["log_expected_minutes", "log_middle_minutes", "confidence"];

    private readonly double _intercept;
    private readonly double[] _coefficients;
    private readonly HashSet<string> _options;

    public static string DefaultJson { get; } = ReadResource();
    public static JevEstimator Default { get; } = Load(DefaultJson);

    /// <summary>The model the weights were fitted against, as Jev reported it.</summary>
    public string Model { get; }
    public string QuestionId { get; }
    public IReadOnlyDictionary<string, JevQuestion> Questions { get; }

    private JevEstimator(string model, string questionId,
        IReadOnlyDictionary<string, JevQuestion> questions, double intercept, double[] coefficients)
    {
        Model = model;
        QuestionId = questionId;
        Questions = questions;
        _intercept = intercept;
        _coefficients = coefficients;
        _options = ((ChoiceQuestion)questions[questionId]).Options.Keys.ToHashSet();
    }

    public static JevEstimator Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var questionId = root.GetProperty("question_id").GetString()!;
        var questions = JevQuestionSet.Parse(root.GetProperty("questions").GetRawText());
        if (!questions.TryGetValue(questionId, out var question) || question is not ChoiceQuestion)
            throw new InvalidOperationException($"The estimator's question '{questionId}' is missing or not a choice.");

        // A refit that changed the features without changing this class would otherwise load
        // cleanly and weight the wrong numbers.
        var features = root.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray();
        if (!features.SequenceEqual(ExpectedFeatures))
            throw new InvalidOperationException(
                $"The estimator was fitted on features [{string.Join(", ", features)}], "
                + $"but this build computes [{string.Join(", ", ExpectedFeatures)}].");

        return new JevEstimator(
            root.GetProperty("model").GetString() ?? "",
            questionId,
            questions,
            root.GetProperty("intercept").GetDouble(),
            root.GetProperty("coefficients").EnumerateArray().Select(c => c.GetDouble()).ToArray());
    }

    public JevEstimate Estimate(ChoiceAnswer answer)
    {
        var options = new List<(int Minutes, double P)>();
        foreach (var (id, p) in answer.Probabilities)
        {
            if (!_options.Contains(id) ||
                !int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                throw new JevException($"Jev answered with option '{id}', which the estimator was not fitted on.");
            options.Add((minutes, p));
        }

        var total = options.Sum(o => o.P);
        if (options.Count == 0 || total <= 0)
            throw new JevException("Jev's answer carried no probabilities.");
        options.Sort((a, b) => a.Minutes.CompareTo(b.Minutes));

        // Same reading as the fit script: normalise, then the average, and the first step at
        // which the cumulative probability reaches one half.
        var expected = options.Sum(o => o.Minutes * o.P) / total;
        var middle = options[^1].Minutes;
        var cumulative = 0.0;
        foreach (var (minutes, p) in options)
        {
            cumulative += p / total;
            if (cumulative >= 0.5) { middle = minutes; break; }
        }

        double[] features = [Math.Log(expected), Math.Log(middle), answer.Confidence];
        var logMinutes = _intercept + features.Zip(_coefficients, (f, c) => f * c).Sum();
        return new JevEstimate(expected, middle, answer.Confidence, Math.Exp(logMinutes));
    }

    private static string ReadResource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
