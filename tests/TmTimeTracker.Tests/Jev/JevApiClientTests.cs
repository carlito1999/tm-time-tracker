using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Jev;
using TmTimeTracker.Tests.Data;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jev;

/// <summary>
/// The shapes below are the documented System One request and response, as TypeSafe serves them
/// and as OpenRouter forwards them. OpenRouter adds id, provider and usage.cost; nothing else
/// differs, which is why one client serves both.
/// </summary>
public class JevApiClientTests : IDisposable
{
    private const string Path = "/v1/systemone";

    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private sealed class PassthroughProtector : TmTimeTracker.Platform.ITokenProtector
    {
        public byte[] Protect(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    }

    private JevCredentialRepository _credentials = null!;

    private JevApiClient New(string? provider = "openrouter", string key = "sk-or-v1-test")
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        _credentials = new JevCredentialRepository(factory, new PassthroughProtector());
        if (provider is not null) _credentials.Save(provider, key);

        return new JevApiClient(
            new HttpClient(), _credentials, NullLogger<JevApiClient>.Instance,
            baseUrlOverride: _server.Url);
    }

    private static readonly Dictionary<string, JevQuestion> Refund = new()
    {
        ["refund"] = new NoulQuestion("Is the customer asking for money back?")
    };

    private void Stub(object body, int status = 200) =>
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    private static object NoulResponse(double p = 0.98) => new
    {
        id = "gen-dec-1789738314-X5e5eKGQdvR9rblyX250",
        model = "typesafe/jev-1.13-20260917",
        provider = "TypeSafe",
        answers = new { refund = new { type = "noul", noul = p } },
        usage = new { input_tokens = 275, output_tokens = 20, cost = 0.00003 }
    };

    private JsonElement SentBody() =>
        JsonDocument.Parse(_server.LogEntries.Single().RequestMessage.Body!).RootElement;

    [Fact]
    public async Task Sends_the_state_the_questions_and_the_providers_pinned_model()
    {
        Stub(NoulResponse());

        await New().AskAsync("I was charged twice.", Refund, CancellationToken.None);

        var body = SentBody();
        body.GetProperty("model").GetString().Should().Be("typesafe/jev-1.13");
        body.GetProperty("state").GetString().Should().Be("I was charged twice.");
        var q = body.GetProperty("questions").GetProperty("refund");
        q.GetProperty("type").GetString().Should().Be("noul");
        q.GetProperty("instructions").GetString().Should().Be("Is the customer asking for money back?");
    }

    [Fact]
    public async Task Sends_the_saved_key_as_a_bearer_token()
    {
        Stub(NoulResponse());

        await New(key: "sk-or-v1-secret").AskAsync("x", Refund, CancellationToken.None);

        _server.LogEntries.Single().RequestMessage.Headers!["Authorization"]
            .Should().ContainSingle().Which.Should().Be("Bearer sk-or-v1-secret");
    }

    [Fact]
    public async Task A_TypeSafe_key_is_sent_with_TypeSafe_s_own_model_id()
    {
        Stub(NoulResponse());

        await New(provider: "typesafe").AskAsync("x", Refund, CancellationToken.None);

        SentBody().GetProperty("model").GetString().Should().Be("jev-1.13.0");
    }

    // State may be structured: named fields keep the ticket's parts apart for the model.
    [Fact]
    public async Task Sends_structured_state_as_a_json_object()
    {
        Stub(NoulResponse());

        await New().AskAsync(new { summary = "Fix login", description = "It 500s" }, Refund,
            CancellationToken.None);

        var state = SentBody().GetProperty("state");
        state.GetProperty("summary").GetString().Should().Be("Fix login");
        state.GetProperty("description").GetString().Should().Be("It 500s");
    }

    [Fact]
    public async Task Serialises_each_question_type_with_its_criteria()
    {
        Stub(NoulResponse());
        var questions = new Dictionary<string, JevQuestion>
        {
            ["repro"] = new NoulQuestion("Are reproduction steps given?",
                WhenTrue: "Steps are written out", WhenFalse: "No steps"),
            ["area"] = new ChoiceQuestion("Which layer does the change touch?",
                new Dictionary<string, string> { ["ui"] = "Screens", ["api"] = "Endpoints" }),
            ["scope"] = new ScoreQuestion("How much code changes?",
                new[] { "One line", "One file", "Several files" })
        };

        await New().AskAsync("x", questions, CancellationToken.None);

        var sent = SentBody().GetProperty("questions");
        var repro = sent.GetProperty("repro").GetProperty("criteria");
        repro.GetProperty("true").GetString().Should().Be("Steps are written out");
        repro.GetProperty("false").GetString().Should().Be("No steps");

        var area = sent.GetProperty("area");
        area.GetProperty("type").GetString().Should().Be("choice");
        area.GetProperty("criteria").GetProperty("api").GetString().Should().Be("Endpoints");

        var scope = sent.GetProperty("scope");
        scope.GetProperty("type").GetString().Should().Be("score");
        scope.GetProperty("criteria").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("One line", "One file", "Several files");
    }

    // OpenRouter's own example sends a noul with no criteria at all, and a "criteria": null the
    // API never documented is a needless way to earn a 422.
    [Fact]
    public async Task Leaves_criteria_out_of_a_noul_that_has_none()
    {
        Stub(NoulResponse());

        await New().AskAsync("x", Refund, CancellationToken.None);

        SentBody().GetProperty("questions").GetProperty("refund")
            .TryGetProperty("criteria", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reads_a_noul_answer()
    {
        Stub(NoulResponse(p: 0.98));

        var result = await New().AskAsync("x", Refund, CancellationToken.None);

        result.Noul("refund").Probability.Should().Be(0.98);
        result.Model.Should().Be("typesafe/jev-1.13-20260917");
    }

    [Fact]
    public async Task Reads_a_choice_answer_with_its_distribution()
    {
        Stub(new
        {
            model = "jev-1.13.0",
            answers = new
            {
                department = new
                {
                    type = "choice", choice = "returns", confidence = 0.9,
                    probabilities = new { shipping = 0.05, returns = 0.95, billing = 0.0 }
                }
            },
            usage = new { input_tokens = 328, output_tokens = 34 }
        });

        var result = await New().AskAsync("x", Refund, CancellationToken.None);

        var answer = result.Choice("department");
        answer.Choice.Should().Be("returns");
        answer.Confidence.Should().Be(0.9);
        answer.Probabilities.Should().Contain("shipping", 0.05);
    }

    [Fact]
    public async Task Reads_a_score_answer_with_per_level_probabilities()
    {
        Stub(new
        {
            model = "jev-1.13.0",
            answers = new
            {
                bug_severity = new
                {
                    type = "score", score = 1.43, confidence = 0.35,
                    legend = new Dictionary<string, string> { ["0"] = "a", ["1"] = "b", ["2"] = "c" },
                    probabilities = new Dictionary<string, double> { ["0"] = 0.0, ["1"] = 0.57, ["2"] = 0.43 }
                }
            },
            usage = new { input_tokens = 332, output_tokens = 18 }
        });

        var result = await New().AskAsync("x", Refund, CancellationToken.None);

        var answer = result.Score("bug_severity");
        answer.Score.Should().Be(1.43);
        answer.Confidence.Should().Be(0.35);
        answer.Probabilities.Should().Equal(0.0, 0.57, 0.43);
    }

    [Fact]
    public async Task Reads_OpenRouter_s_cost_and_token_usage()
    {
        Stub(NoulResponse());

        var result = await New().AskAsync("x", Refund, CancellationToken.None);

        result.Usage.Should().Be(new JevUsage(275, 20, 0.00003m));
    }

    [Fact]
    public async Task A_direct_TypeSafe_response_has_no_cost()
    {
        Stub(new
        {
            model = "jev-1.13.0",
            answers = new { refund = new { type = "noul", noul = 0.5 } },
            usage = new { input_tokens = 296, output_tokens = 20 }
        });

        var result = await New(provider: "typesafe").AskAsync("x", Refund, CancellationToken.None);

        result.Usage.CostUsd.Should().BeNull();
    }

    // No key is the normal state for anyone who never opted in, so callers must be able to see it
    // without provoking an exception.
    [Fact]
    public async Task Without_a_saved_key_it_is_not_configured_and_asking_throws()
    {
        var client = New(provider: null);

        client.IsConfigured.Should().BeFalse();
        var act = () => client.AskAsync("x", Refund, CancellationToken.None);
        await act.Should().ThrowAsync<JevException>().Where(e => e.StatusCode == null);
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public void A_saved_key_makes_it_configured()
    {
        New().IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_an_auth_failure()
    {
        Stub(new { error = new { message = "No auth credentials found", code = 401 } }, status: 401);

        var act = () => New().AskAsync("x", Refund, CancellationToken.None);

        (await act.Should().ThrowAsync<JevException>()).Which.IsAuthFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task A_rate_limit_or_overload_is_retried_once(int status)
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .InScenario("busy").WillSetStateTo("retried")
               .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .InScenario("busy").WhenStateIs("retried")
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(NoulResponse()));

        var result = await New().AskAsync("x", Refund, CancellationToken.None);

        result.Noul("refund").Probability.Should().Be(0.98);
        _server.LogEntries.Should().HaveCount(2);
    }

    // Jev runs inside the estimation sweep now, so an hour-long Retry-After must not park the
    // sweep for an hour. The cap is a constructor argument so this test need not sleep at all.
    [Fact]
    public async Task A_long_Retry_After_is_capped()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .InScenario("slow").WillSetStateTo("retried")
               .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "3600"));
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .InScenario("slow").WhenStateIs("retried")
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(NoulResponse()));
        New();
        var client = new JevApiClient(new HttpClient(), _credentials, NullLogger<JevApiClient>.Instance,
            baseUrlOverride: _server.Url, maxRetryWait: TimeSpan.Zero);

        var ask = client.AskAsync("x", Refund, CancellationToken.None);

        (await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(ask);
        (await ask).Noul("refund").Probability.Should().Be(0.98);
    }

    [Fact]
    public async Task A_second_rate_limit_gives_up_with_the_status()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));

        var act = () => New().AskAsync("x", Refund, CancellationToken.None);

        (await act.Should().ThrowAsync<JevException>()).Which.StatusCode.Should().Be(429);
        _server.LogEntries.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_success_status_without_answers_is_rejected_as_unreadable()
    {
        Stub(new { model = "jev-1.13.0" });

        var act = () => New().AskAsync("x", Refund, CancellationToken.None);

        await act.Should().ThrowAsync<JevException>();
    }

    // Settings tests a pasted key before storing it, so the check must use the key it is handed
    // and leave whatever is already saved alone.
    [Fact]
    public async Task Check_uses_the_key_it_is_given_and_saves_nothing()
    {
        Stub(NoulResponse());
        var client = New(key: "sk-or-v1-old");

        await client.CheckAsync(new JevCredential("typesafe", "new-key"), CancellationToken.None);

        var request = _server.LogEntries.Single().RequestMessage;
        request.Headers!["Authorization"].Should().ContainSingle().Which.Should().Be("Bearer new-key");
        SentBody().GetProperty("model").GetString().Should().Be("jev-1.13.0");
        _credentials.Get().Should().Be(new JevCredential("openrouter", "sk-or-v1-old"));
    }
}
