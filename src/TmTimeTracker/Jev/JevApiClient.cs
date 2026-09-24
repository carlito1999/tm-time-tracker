using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;

namespace TmTimeTracker.Jev;

public interface IJevClient
{
    /// <summary>False when no key is saved, which is the normal state for anyone not using Jev.</summary>
    bool IsConfigured { get; }

    /// <summary>Asks the questions about the state with the saved key.</summary>
    /// <param name="state">A string, or anything that serialises to a JSON object or array.</param>
    Task<JevResult> AskAsync(
        object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct);

    /// <summary>
    /// One minimal question with the given key rather than the saved one, so Settings can prove a
    /// pasted key works before storing it.
    /// </summary>
    Task<JevResult> CheckAsync(JevCredential credential, CancellationToken ct);
}

/// <summary>
/// Calls Jev, TypeSafe's System One model, through whichever provider the saved key belongs to.
///
/// There is no .NET SDK, and none is needed: the whole API is one POST whose body OpenRouter
/// forwards to TypeSafe unchanged. The key is read on every call rather than once at startup, so
/// a key saved in Settings takes effect without restarting the daemon.
///
/// Failures throw <see cref="JevException"/> instead of returning null, unlike the GitLab client:
/// Settings has to show why a key was refused, and a caller that treats Jev as optional can catch
/// one exception type.
/// </summary>
public sealed class JevApiClient : IJevClient
{
    private static readonly Dictionary<string, JevQuestion> ProbeQuestions = new()
    {
        ["greeting"] = new NoulQuestion("Does this message open with a greeting?")
    };
    private const string ProbeState = "Hi there, could you tell me where my order is?";

    private readonly HttpClient _http;
    private readonly JevCredentialRepository _credentials;
    private readonly ILogger<JevApiClient> _log;
    private readonly string? _baseUrlOverride;

    // Jev is called from inside the estimation sweep, which is serialised: a provider asking for
    // an hour's pause must not hold every other repo's estimates for that hour. One capped retry,
    // then the caller falls back.
    private readonly TimeSpan _maxRetryWait;

    public JevApiClient(
        HttpClient http,
        JevCredentialRepository credentials,
        ILogger<JevApiClient> log,
        string? baseUrlOverride = null,
        TimeSpan? maxRetryWait = null)
    {
        _http = http;
        _credentials = credentials;
        _log = log;
        _baseUrlOverride = baseUrlOverride?.TrimEnd('/');
        _maxRetryWait = maxRetryWait ?? TimeSpan.FromSeconds(10);
    }

    public bool IsConfigured => Usable(_credentials.Get()) is not null;

    public Task<JevResult> AskAsync(
        object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct)
    {
        var credential = _credentials.Get();
        var provider = Usable(credential)
            ?? throw new JevException("No Jev key saved. Add one on the API tokens tab in Settings.");
        return SendAsync(provider, credential!.Token, state, questions, ct);
    }

    public Task<JevResult> CheckAsync(JevCredential credential, CancellationToken ct)
    {
        var provider = JevProvider.FromId(credential.Provider)
            ?? throw new JevException($"Unknown Jev provider '{credential.Provider}'.");
        return SendAsync(provider, credential.Token, ProbeState, ProbeQuestions, ct);
    }

    private static JevProvider? Usable(JevCredential? credential) =>
        credential is null || string.IsNullOrWhiteSpace(credential.Token)
            ? null
            : JevProvider.FromId(credential.Provider);

    private async Task<JevResult> SendAsync(
        JevProvider provider, string token, object state,
        IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct)
    {
        var endpoint = _baseUrlOverride is null ? provider.Endpoint : _baseUrlOverride + JevProvider.Path;
        var body = BuildBody(provider.Model, state, questions);

        // One retry, on the two statuses the API documents as transient: 429 rate limited and 529
        // overloaded. Anything else - a bad key, a malformed question - fails the same way twice.
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new JevException($"Could not reach {provider.DisplayName}: {ex.Message}", inner: ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode) return Parse(text);

                if ((status is 429 or 529) && attempt == 1)
                {
                    var asked = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    var wait = asked < _maxRetryWait ? asked : _maxRetryWait;
                    _log.LogWarning("Jev answered {Status} via {Provider}; retrying once after {Seconds}s",
                        status, provider.DisplayName, wait.TotalSeconds);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                // The endpoint is safe to log: the key travels as a header and never appears in it.
                _log.LogInformation("Jev returned {Status} from {Endpoint}", status, endpoint);
                throw new JevException($"{provider.DisplayName} answered {status}: {Trim(text)}", status);
            }
        }
    }

    private static string BuildBody(
        string model, object state, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        var questionsJson = new JsonObject();
        foreach (var (id, question) in questions) questionsJson[id] = question.ToJson();

        return new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(state),
            ["questions"] = questionsJson
        }.ToJsonString();
    }

    private static JevResult Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
                throw new JevException($"Jev's response carried no answers: {Trim(text)}");

            var parsed = new Dictionary<string, JevAnswer>();
            foreach (var answer in answers.EnumerateObject())
                parsed[answer.Name] = ParseAnswer(answer.Name, answer.Value);

            var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            return new JevResult(model, parsed, ParseUsage(root));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                        or FormatException)
        {
            throw new JevException($"Jev's response could not be read: {Trim(text)}", inner: ex);
        }
    }

    private static JevAnswer ParseAnswer(string id, JsonElement a) =>
        a.GetProperty("type").GetString() switch
        {
            "noul" => new NoulAnswer(a.GetProperty("noul").GetDouble()),
            "choice" => new ChoiceAnswer(
                a.GetProperty("choice").GetString() ?? "",
                a.GetProperty("confidence").GetDouble(),
                a.GetProperty("probabilities").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetDouble())),
            // Levels arrive as a map keyed "0", "1", ... - put them back in scale order.
            "score" => new ScoreAnswer(
                a.GetProperty("score").GetDouble(),
                a.GetProperty("confidence").GetDouble(),
                a.GetProperty("probabilities").EnumerateObject()
                    .OrderBy(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture))
                    .Select(p => p.Value.GetDouble())
                    .ToList()),
            var other => throw new JevException($"Jev answered '{id}' with unknown type '{other}'.")
        };

    private static JevUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return new JevUsage(0, 0, null);

        int Int(string name) => usage.TryGetProperty(name, out var v) ? v.GetInt32() : 0;

        // Read as a double: OpenRouter's cost is often tiny enough to arrive in exponent form.
        decimal? cost = usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
            ? (decimal)c.GetDouble()
            : null;

        return new JevUsage(Int("input_tokens"), Int("output_tokens"), cost);
    }

    private static string Trim(string body) =>
        body.Length <= 180 ? body : body[..180] + "…";
}
