using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TmTimeTracker;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jev;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.UI;

// Clicking a toast COM-activates this exe. RunDaemon's SingleInstanceGuard would stop the second
// daemon anyway, but exiting here means a toast click does no work at all rather than racing it.
//
// Guarded because this runs before AppPaths and before any logger exists: if the WinRT contract
// is missing or COM is in an odd state, a throw here would kill the daemon at startup with
// nowhere to report why. Degrading to "possibly two instances" beats "will not start".
try
{
    if (Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        return;
}
catch
{
    // Fall through to normal startup.
}

AppPaths.EnsureExists();

if (args.Length >= 1 && args[0] == "--login")     { await RunCli(b => b, RunLogin); return; }
if (args.Length == 2 && args[0] == "--probe-jira"){ await RunCli(b => b, h => RunProbe(h, args[1])); return; }
if (args.Length == 3 && args[0] == "--probe-pr")
    { await RunCli(b => b, h => RunPullRequestProbe(h, args[1], args[2])); return; }
if (args.Length == 3 && args[0] == "--set-jira-token")
    { await RunCli(b => b, h => SetJiraToken(h, args[1], args[2])); return; }
if (args.Length == 3 && args[0] == "--set-bitbucket-token")
    { await RunCli(b => b, h => SetBitbucketToken(h, args[1], args[2])); return; }
// Two arguments, not three: GitLab authenticates with the token alone, and the username is read
// back from the API rather than typed.
if (args.Length == 2 && args[0] == "--set-gitlab-token")
    { await RunCli(b => b, h => SetGitLabToken(h, args[1])); return; }
// Offline experiments: asks Jev a question set about many tickets with the stored key. The key
// stays inside this process - the output carries states and answers, never the credential.
if (args.Length == 4 && args[0] == "--jev-batch")
    { await RunCli(b => b.AddClaudeServices(), h => RunJevBatch(h, args[1], args[2], args[3])); return; }
// The overdue warning cannot fire until the read:dev-info:jira scope is granted, so this is the
// only way to see a real toast come out of the published exe.
if (args.Length == 1 && args[0] == "--test-toast")
{
    // Deliberately NOT going through WindowsToastNotifier: its balloon fallback swallows the
    // failure, which is right in production and useless for verifying that toasts work at all.
    try
    {
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml(WindowsToastNotifier.BuildXml(
            "TmTimeTracker", "Test notification - this one is marked urgent.", urgent: true));
        Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat
            .CreateToastNotifier()
            .Show(new Windows.UI.Notifications.ToastNotification(xml));
        Console.WriteLine("Toast handed to Windows without error.");
        // Windows delivers it asynchronously; exiting immediately can cut it off.
        await Task.Delay(TimeSpan.FromSeconds(2));
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Toast failed: {ex}");
        Environment.ExitCode = 1;
        return;
    }
}
if (args.Length == 1 && args[0] == "--smoke-activity")
    { await RunCli(b => b.AddActivityServices(), RunStreaming); return; }
if (args.Length == 1 && args[0] == "--smoke-poll")
    { await RunCli(b => b.AddActivityServices().AddPollGate().AddPollServices(),
                   h => { h.Services.GetRequiredService<PollServiceGate>().Enabled = true; return RunStreaming(h); }); return; }

await RunDaemon();

static IHostBuilder BaseBuilder() =>
    Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c => c.AddJsonFile("secrets.json", optional: true, reloadOnChange: false))
        .ConfigureServices((ctx, services) =>
        {
            var secrets = ctx.Configuration.Get<AppSecrets>() ?? new AppSecrets();
            services.AddSingleton(secrets);
        })
        .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
        .AddTmTimeTrackerCore()
        .AddJiraServices();

static async Task RunCli(Func<IHostBuilder, IHostBuilder> compose, Func<IHost, Task> body)
{
    var host = compose(BaseBuilder()).Build();
    using var scope = host.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
    SeedConfigIfMissing(host);
    await body(host);
}

static async Task RunDaemon()
{
    using var guard = new SingleInstanceGuard();
    if (!guard.IsPrimary) return;

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            Path.Combine(AppPaths.LogsDir, "daemon-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30)
        .CreateLogger();

    try
    {
        var host = BaseBuilder()
            .UseSerilog()
            .AddActivityServices()
            .AddPollGate()
            .AddPollServices()
            .AddSlackServices()
            .AddClaudeServices()
            .AddTrayUI()
            .Build();

        var sp = host.Services;
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();

        TryMigrateSecretsJson(sp);
        TryMigrateTrackedRepos(sp);

        var appConfig = sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        var oauthState = sp.GetRequiredService<OAuthStateRepository>().Load();
        var cfg = sp.GetRequiredService<ConfigRepository>().TryGet();
        var hasTrackedRepo = sp.GetRequiredService<TrackedRepoRepository>().GetAll().Count > 0;

        var setupNeeded = appConfig is null || oauthState is null || cfg is null || !hasTrackedRepo;
        var windows = sp.GetRequiredService<WindowsHost>();
        var gate = sp.GetRequiredService<PollServiceGate>();

        windows.SetupCompleted += () =>
        {
            gate.Enabled = true;
            windows.ShowDashboard();
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) AutoStartRegistrar.Register(path);
        };

        if (setupNeeded)
        {
            var startPage = appConfig is null
                ? SetupWindow.Page.OAuthApp
                : oauthState is null
                    ? SetupWindow.Page.Connect
                    : SetupWindow.Page.Paths;
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                windows.ShowSetup(startPage);
            });
        }
        else
        {
            gate.Enabled = true;
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) AutoStartRegistrar.Register(path);
        }

        await host.RunAsync();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "TmTimeTracker crashed");
        try
        {
            System.Windows.Forms.MessageBox.Show(
                $"TmTimeTracker failed to start.\n\n{ex.GetType().Name}: {ex.Message}\n\nSee log at {AppPaths.LogsDir}",
                "TmTimeTracker", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch { /* if even MessageBox fails, give up quietly */ }
        throw;
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

static void TryMigrateTrackedRepos(IServiceProvider sp)
{
    var trackedRepo = sp.GetRequiredService<TrackedRepoRepository>();
    if (trackedRepo.GetAll().Count > 0) return;

    var cfg = sp.GetRequiredService<ConfigRepository>().TryGet();
    if (cfg is null || string.IsNullOrWhiteSpace(cfg.RepoPath)) return;
    if (!Directory.Exists(cfg.RepoPath)) return;

    trackedRepo.Add(cfg.RepoPath);
}

static void TryMigrateSecretsJson(IServiceProvider sp)
{
    var repo = sp.GetRequiredService<OAuthAppConfigRepository>();
    if (repo.Load() is not null) return;
    const string path = "secrets.json";
    if (!File.Exists(path)) return;
    try
    {
        using var stream = File.OpenRead(path);
        var doc = System.Text.Json.JsonDocument.Parse(stream);
        var atl = doc.RootElement.GetProperty("Atlassian");
        var id = atl.GetProperty("OAuthClientId").GetString() ?? "";
        var secret = atl.GetProperty("OAuthClientSecret").GetString() ?? "";
        var redirect = atl.TryGetProperty("RedirectUri", out var r)
            ? r.GetString() ?? "http://localhost:53682/callback"
            : "http://localhost:53682/callback";
        if (id == "REPLACE_ME" || string.IsNullOrEmpty(id)) return;
        repo.Save(new OAuthAppConfig(id, secret, redirect));
        try { File.Move(path, "secrets.json.migrated", overwrite: true); } catch { /* best-effort */ }
    }
    catch { /* migration is best-effort; ignore */ }
}

// Diagnostic: the development-information endpoint is undocumented, so this reports what the
// app's OAuth token can actually reach - the api.atlassian.com gateway, the site host, or neither.
static async Task SetJiraToken(IHost host, string email, string token)
{
    host.Services.GetRequiredService<JiraApiTokenRepository>().Save(email, token);
    Console.WriteLine($"Stored Jira API token for {email} (DPAPI-encrypted).");
    await Task.CompletedTask;
}

static async Task SetBitbucketToken(IHost host, string email, string token)
{
    host.Services.GetRequiredService<BitbucketApiTokenRepository>().Save(email, token);
    Console.WriteLine($"Stored Bitbucket API token for {email} (DPAPI-encrypted).");
    await Task.CompletedTask;
}

// Probes before storing, because the failure this exists to catch is a token with the wrong
// scope: a git-access credential reaches GitLab and is refused only at /api/v4, so storing an
// unchecked token would look like success and fail silently on the first ticket.
static async Task SetGitLabToken(IHost host, string token)
{
    using var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://gitlab.com/api/v4/user");
    request.Headers.Add("PRIVATE-TOKEN", token);

    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"GitLab refused the token: {(int)response.StatusCode} {body}");
        Console.Error.WriteLine("It needs a Personal Access Token carrying read_api. Create one at");
        Console.Error.WriteLine("  https://gitlab.com/-/user_settings/personal_access_tokens");
        Environment.ExitCode = 1;
        return;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var username = doc.RootElement.TryGetProperty("username", out var u)
        ? u.GetString()
        : null;
    if (string.IsNullOrWhiteSpace(username)) username = "gitlab";

    host.Services.GetRequiredService<GitLabApiTokenRepository>().Save(username, token);
    Console.WriteLine($"Stored GitLab API token for {username} (DPAPI-encrypted).");
}

// Exercises the same source the worker uses - Bitbucket first, Jira's dev-status only as the
// fallback - so a green probe means the worker will work too.
static async Task RunPullRequestProbe(IHost host, string issueId, string ticketKey)
{
    var credential = host.Services.GetRequiredService<JiraApiTokenRepository>().Get();
    if (credential is null)
    {
        Console.Error.WriteLine("No Jira API token stored. Set one with:");
        Console.Error.WriteLine("  TmTimeTracker.exe --set-jira-token <atlassian-email> <api-token>");
        Console.Error.WriteLine("Create one at https://id.atlassian.com/manage-profile/security/api-tokens");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Using basic auth as {credential.Email}.");

    var snapshot = await host.Services.GetRequiredService<IPullRequestSource>()
        .GetSnapshotAsync(issueId, ticketKey, CancellationToken.None);

    var lastCommit = snapshot.LastCommitUtc?.ToString("u") ?? "(none)";
    Console.WriteLine($"last commit: {lastCommit}");
    if (snapshot.PullRequest is null)
    {
        Console.WriteLine("pull request: (none reported)");
        return;
    }

    Console.WriteLine($"pull request: {snapshot.PullRequest.Status} - {snapshot.PullRequest.Title}");
    Console.WriteLine($"         url: {snapshot.PullRequest.Url}");
}
static void SeedConfigIfMissing(IHost host)
{
    using var scope = host.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: Environment.CurrentDirectory,
        RememberPath: Path.Combine(Environment.CurrentDirectory, ".remember"),
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));
}

static async Task RunLogin(IHost host)
{
    var coord = host.Services.GetRequiredService<OAuthCoordinator>();
    var oauth = host.Services.GetRequiredService<JiraOAuthClient>();
    var listener = host.Services.GetRequiredService<LocalCallbackListener>();
    var src = host.Services.GetRequiredService<IOAuthAppConfigSource>();

    var state = Guid.NewGuid().ToString("N");
    var url = oauth.BuildAuthorizationUrl(state);
    Console.WriteLine("Opening browser to:");
    Console.WriteLine(url);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });

    var redirect = new Uri(src.Get().RedirectUri);
    var listenerPrefix = $"{redirect.Scheme}://{redirect.Authority}/";
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var cb = await listener.ListenOnceAsync(listenerPrefix, cts.Token);
    if (cb.State != state) { Console.WriteLine("State mismatch - aborting."); return; }
    await coord.CompleteFirstRunAsync(cb.Code, cts.Token);
    Console.WriteLine("Login complete. Tokens stored.");
}

static async Task RunProbe(IHost host, string ticketKey)
{
    var api = host.Services.GetRequiredService<JiraApiClient>();
    var issue = await api.GetIssueAsync(ticketKey, CancellationToken.None);
    Console.WriteLine($"{issue.Key}: {issue.Fields.Status.Name} ({issue.Fields.Status.Category.Key})");
}

static async Task RunStreaming(IHost host)
{
    var bus = host.Services.GetRequiredService<IEventBus>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    _ = Task.Run(async () =>
    {
        await foreach (var evt in bus.Subscribe(cts.Token))
            Console.WriteLine($"[{evt.AtUtc:HH:mm:ss}] {evt.GetType().Name}: {evt}");
    });
    await host.RunAsync(cts.Token);
}

// One JSON line per ticket: the state Jev read and its answers. Resumable - a key already in the
// output is skipped - so a run cut short by a rate limit or a closed laptop picks up where it
// stopped rather than paying for the same answers twice.
static async Task RunJevBatch(IHost host, string questionsPath, string keysPath, string outPath)
{
    var jev = host.Services.GetRequiredService<IJevClient>();
    if (!jev.IsConfigured)
    {
        Console.Error.WriteLine("No Jev key saved. Add one on the API tokens tab in Settings.");
        Environment.ExitCode = 1;
        return;
    }

    var questions = JevQuestionSet.Parse(await File.ReadAllTextAsync(questionsPath));
    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(outPath))
        foreach (var line in await File.ReadAllLinesAsync(outPath))
            if (line.Length > 0)
                done.Add(System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("key").GetString()!);

    var todo = (await File.ReadAllLinesAsync(keysPath))
        .Select(k => k.Trim()).Where(k => k.Length > 0 && !done.Contains(k))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var jira = host.Services.GetRequiredService<IJiraSearchSource>();
    var gitlab = host.Services.GetRequiredService<TmTimeTracker.GitLab.IGitLabIssueSource>();
    var snake = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    await using var writer = new StreamWriter(outPath, append: true);
    var answered = 0;
    var cost = 0m;

    foreach (var chunk in todo.Chunk(50))
    {
        foreach (var issue in await SearchKeysAsync(jira, chunk))
        {
            var linked = new List<LinkedIssue>();
            foreach (var link in GitLabIssueLink.FindAll(AdfText.Urls(issue.Fields.Description)))
                if (await gitlab.FetchAsync(link, CancellationToken.None) is { } found) linked.Add(found);

            var state = JevTicketState.Build(
                issue.Fields.Summary, AdfText.Flatten(issue.Fields.Description),
                (issue.Fields.Attachments ?? []).Select(a => a.Filename).ToList(), linked);

            JevResult result;
            try
            {
                result = await jev.AskAsync(state, questions, CancellationToken.None);
            }
            catch (JevException ex)
            {
                Console.Error.WriteLine($"{issue.Key}: {ex.Message}");
                if (ex.IsAuthFailure) { Environment.ExitCode = 1; return; }
                continue;
            }

            var answers = new System.Text.Json.Nodes.JsonObject();
            foreach (var (id, answer) in result.Answers)
            {
                var node = System.Text.Json.JsonSerializer.SerializeToNode(answer, answer.GetType(), snake)!.AsObject();
                node["type"] = answer.GetType().Name.Replace("Answer", "").ToLowerInvariant();
                answers[id] = node;
            }

            await writer.WriteLineAsync(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = issue.Key,
                ["model"] = result.Model,
                ["input_tokens"] = result.Usage.InputTokens,
                ["cost"] = result.Usage.CostUsd,
                ["state"] = System.Text.Json.JsonSerializer.SerializeToNode(state),
                ["answers"] = answers
            }.ToJsonString());
            await writer.FlushAsync();

            answered++;
            cost += result.Usage.CostUsd ?? 0;
        }
    }

    Console.WriteLine($"Answered {answered} of {todo.Count} tickets ({done.Count} already done). Cost ${cost:0.#####}.");
}

// A key Jira cannot see - deleted, or in a project this account cannot read - fails the whole
// `key in (...)` query, so a failed chunk is retried one key at a time and the missing key dropped.
static async Task<IReadOnlyList<Issue>> SearchKeysAsync(IJiraSearchSource jira, string[] keys)
{
    try
    {
        return await jira.SearchIssuesAsync($"key in ({string.Join(",", keys)})", CancellationToken.None);
    }
    catch (HttpRequestException) when (keys.Length > 1)
    {
        var found = new List<Issue>();
        foreach (var key in keys) found.AddRange(await SearchKeysAsync(jira, [key]));
        return found;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"{keys[0]}: not readable from Jira ({ex.StatusCode}).");
        return [];
    }
}
