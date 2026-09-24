using System.Text.Json;
using FluentAssertions;
using TmTimeTracker.Jev;
using Xunit;

namespace TmTimeTracker.Tests.Jev;

/// <summary>
/// The weights come from tools/jev/fit_estimator.py. These tests hold the C# side to the Python
/// side: a mismatch in how the distribution is read would silently skew every estimate.
/// </summary>
public class JevEstimatorTests
{
    private static ChoiceAnswer Answer(double confidence, params (int Minutes, double P)[] probs) =>
        new(probs.MaxBy(p => p.P).Minutes is var m ? $"m{m}" : "",
            confidence,
            probs.ToDictionary(p => $"m{p.Minutes}", p => p.P));

    [Fact]
    public void The_embedded_estimator_asks_the_fifteen_minute_question()
    {
        var estimator = JevEstimator.Default;

        var question = estimator.Questions[estimator.QuestionId].Should().BeOfType<ChoiceQuestion>().Subject;
        question.Options.Should().HaveCount(32);
        question.Options.Keys.Should().StartWith("m15").And.EndWith("m480");
    }

    // The fit script stores one real answer and the minutes it predicted for it. Reproducing
    // that figure is what proves the two sides read the distribution the same way.
    [Fact]
    public void Reproduces_the_fit_scripts_own_prediction_for_its_check_case()
    {
        var json = JsonDocument.Parse(JevEstimator.DefaultJson).RootElement.GetProperty("check");
        var answer = json.GetProperty("answer");
        var choice = new ChoiceAnswer(
            answer.GetProperty("choice").GetString()!,
            answer.GetProperty("confidence").GetDouble(),
            answer.GetProperty("probabilities").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetDouble()));

        var estimate = JevEstimator.Default.Estimate(choice);

        estimate.RawMinutes.Should().BeApproximately(json.GetProperty("minutes").GetDouble(), 0.01);
    }

    [Fact]
    public void Reads_the_average_and_the_middle_of_the_distribution()
    {
        var estimate = JevEstimator.Default.Estimate(Answer(0.2, (30, 0.2), (60, 0.5), (240, 0.3)));

        estimate.ExpectedMinutes.Should().BeApproximately(0.2 * 30 + 0.5 * 60 + 0.3 * 240, 1e-9);
        estimate.MiddleMinutes.Should().Be(60);
        estimate.Confidence.Should().Be(0.2);
    }

    [Fact]
    public void An_answer_leaning_longer_gives_a_longer_estimate()
    {
        var shortOne = JevEstimator.Default.Estimate(Answer(0.3, (30, 0.7), (45, 0.3)));
        var longOne = JevEstimator.Default.Estimate(Answer(0.3, (240, 0.7), (300, 0.3)));

        longOne.Minutes.Should().BeGreaterThan(shortOne.Minutes);
    }

    [Fact]
    public void Minutes_is_the_raw_prediction_rounded_to_a_whole_minute()
    {
        var estimate = JevEstimator.Default.Estimate(Answer(0.5, (90, 1.0)));

        estimate.Minutes.Should().Be((int)Math.Round(estimate.RawMinutes));
        estimate.Minutes.Should().BePositive();
    }

    // An option id the estimator was not fitted on means the question and the weights have
    // drifted apart; guessing a figure from it would be worse than falling back.
    [Fact]
    public void Rejects_an_option_it_was_not_fitted_on()
    {
        var answer = new ChoiceAnswer("soon", 0.5, new Dictionary<string, double> { ["soon"] = 1.0 });

        var act = () => JevEstimator.Default.Estimate(answer);

        act.Should().Throw<JevException>();
    }
}
