using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jev;
using TmTimeTracker.Services;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI.SettingsPages;

/// <summary>
/// The three API tokens this app needs, side by side.
///
/// The two Atlassian ones come from the same page and look identical, but are not
/// interchangeable - the Jira one is scopeless, and Bitbucket rejects it outright. Showing them
/// together, each with its own walkthrough naming the exact button and scope, is the point of
/// this page: the difference is invisible once a token is pasted, and diagnosing it afterwards
/// means reading a 401 body.
///
/// GitLab is a third token again, and unrelated: a Personal Access Token carrying read_api, used
/// to read the issue a ticket links to. It needs no email, because GitLab authenticates with the
/// token alone.
///
/// Jev is a fourth key, for TypeSafe's decision model, and the only one with a choice of issuer:
/// the same model is sold through OpenRouter and by TypeSafe directly, the keys are not
/// interchangeable, and the provider picked here decides which endpoint the key is sent to.
/// </summary>
public sealed class ApiTokensPage : UserControl
{
    private const string TokenPageUrl = "https://id.atlassian.com/manage-profile/security/api-tokens";
    private const string GitLabTokenPageUrl =
        "https://gitlab.com/-/user_settings/personal_access_tokens";

    private readonly IServiceProvider _sp;
    private readonly ILogger _log;
    private readonly TextBox _emailBox;
    private readonly TokenSection _jira;
    private readonly TokenSection _bitbucket;
    private readonly TokenSection _gitlab;
    private readonly TokenSection _jev;
    private readonly ComboBox _jevProvider;

    public ApiTokensPage(IServiceProvider sp, ILogger log)
    {
        _sp = sp;
        _log = log;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        AutoScroll = true;

        _emailBox = new TextBox
        {
            Font = Theme.Mono,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 380,
            Margin = new Padding(0, 0, 8, 0)
        };

        _jira = new TokenSection(
            title: "Jira token — finds the pull request",
            hint: "Reads Jira's development information, which is where the app gets a pull "
                + "request's URL. Required: without it, notifications that use {PR_URL} never post.",
            walkthrough: new[]
            {
                "1.  Press \"Create API token\" — the plain button, NOT the one with scopes.",
                "2.  Name it anything, for example TmTimeTracker.",
                "3.  Copy the token and paste it above, then press Save and test.",
                "",
                "This token needs no scopes at all. Jira accepts a scopeless token; adding scopes "
                + "to it does no harm but is not required."
            },
            onCreate: OpenTokenPage);

        _bitbucket = new TokenSection(
            title: "Bitbucket token — optional",
            hint: "Not used yet. Stored here so it is not lost, for querying Bitbucket directly "
                + "instead of waiting for Jira to ingest the pull request.",
            walkthrough: new[]
            {
                "1.  Press \"Create API token with scopes\" — the OTHER button.",
                "2.  Name it, set an expiry.",
                "3.  Choose the app: Bitbucket.",
                "4.  Tick the scope: read:pullrequest:bitbucket",
                "5.  Copy the token and paste it above, then press Save and test.",
                "",
                "Pick read:pullrequest:bitbucket, not read:repository:bitbucket — the latter sounds "
                + "right but explicitly excludes pull requests. Save and test checks that the token "
                + "reaches Bitbucket; it cannot confirm which scopes it carries."
            },
            onCreate: OpenTokenPage);

        _gitlab = new TokenSection(
            title: "GitLab token \u2014 reads the linked issue",
            hint: "Tickets here are often nothing but a link to a GitLab issue. The app "
                + "reads that issue and puts its text into the estimation prompt; without "
                + "this the estimate is made without ever seeing what the ticket is about.",
            walkthrough: new[]
            {
                "1.  Press \"Add new token\" and keep the CLASSIC token form.",
                "2.  Name it, for example Time tracker, and set an expiry.",
                "3.  Tick exactly one scope: read_api",
                "4.  Copy the token and paste it above, then press Save and test.",
                "",
                "If you land on a \"Resource and permission selector\" with Group "
                + "and project, User and Global tabs, that is the newer fine-grained form and "
                + "it has no read_api checkbox. Go back and choose the classic token instead.",
                "",
                "read_api, not api: this only ever reads. The GitLab sign-in your git client "
                + "already uses cannot be reused - it authenticates git transport only and "
                + "answers 403 insufficient_scope to every API call."
            },
            onCreate: OpenGitLabTokenPage);

        _jevProvider = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(JevProvider.DisplayName),
            Width = 200
        };
        foreach (var provider in JevProvider.All) _jevProvider.Items.Add(provider);
        _jevProvider.SelectedIndex = 0;

        _jev = new TokenSection(
            title: "Jev key — optional, for ticket estimation",
            hint: "With a key saved, the estimate written to a ticket's Original Estimate is "
                + "Jev's, calibrated on your logged time; Claude's figure is kept as the fallback. "
                + "With no key, estimation uses Claude alone.",
            walkthrough: new[]
            {
                "OpenRouter:",
                "1.  Press \"Create API Key\" on the page that just opened.",
                "2.  Name it, for example TmTimeTracker. A credit limit is optional.",
                "3.  Copy the key (it starts sk-or-) and paste it above, then press Save and test.",
                "",
                "Jev is billed to your OpenRouter credit, input tokens only - the test call costs "
                + "a few thousandths of a cent.",
                "",
                "TypeSafe (direct): choose it in the list above first; the button then opens "
                + "console.typesafe.ai/keys instead. A key from one provider is refused by the other."
            },
            onCreate: OpenJevKeyPage);

        _jira.Save.Click += async (_, _) => await SaveJiraAsync();
        _jev.Save.Click += async (_, _) => await SaveJevAsync();
        _gitlab.Save.Click += async (_, _) => await SaveGitLabAsync();
        _bitbucket.Save.Click += async (_, _) => await SaveBitbucketAsync();

        var emailRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 0)
        };
        emailRow.Controls.Add(_emailBox);

        var providerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 0)
        };
        providerRow.Controls.Add(_jevProvider);

        // Docked children stack in reverse order of addition, so add bottom-up.
        foreach (var c in _jev.Controls) Controls.Add(c);
        Controls.Add(providerRow);
        Controls.Add(MakeHint(
            "Which service issued your key. Both serve the same model, but each only accepts "
            + "its own keys."));
        Controls.Add(MakeHeading("Jev decision model"));
        foreach (var c in _gitlab.Controls) Controls.Add(c);
        Controls.Add(MakeHint(
            "A separate GitLab Personal Access Token, unrelated to the two above and created "
            + "somewhere else entirely."));
        Controls.Add(MakeHeading("GitLab API token"));
        foreach (var c in _bitbucket.Controls) Controls.Add(c);
        foreach (var c in _jira.Controls) Controls.Add(c);
        Controls.Add(emailRow);
        Controls.Add(MakeHint(
            "Your Atlassian account email, used with both tokens. It is the address you sign in "
            + "to Atlassian with."));
        Controls.Add(MakeHeading("Atlassian account email"));
        Controls.Add(MakeHint(
            "Both tokens are created on the same Atlassian page, but they are different tokens and "
            + "cannot be swapped. Each section below opens that page and shows you exactly which "
            + "button and scope to pick."));
        Controls.Add(MakeHeading("Atlassian API tokens"));

        Load += (_, _) => Initialise();
    }

    private void Initialise()
    {
        var jira = _sp.GetRequiredService<JiraApiTokenRepository>().Get();
        var bitbucket = _sp.GetRequiredService<BitbucketApiTokenRepository>().Get();

        _emailBox.Text = jira?.Email
                         ?? bitbucket?.Email
                         ?? Environment.GetEnvironmentVariable("ATLASSIAN_EMAIL")
                         ?? string.Empty;

        Describe(_jira, jira, "Jira");
        Describe(_bitbucket, bitbucket, "Bitbucket");

        // Deliberately kept out of _emailBox: that field is the Atlassian account address, and
        // this credential carries a GitLab username in its place.
        Describe(_gitlab, _sp.GetRequiredService<GitLabApiTokenRepository>().Get(), "GitLab");

        DescribeJev(_sp.GetRequiredService<JevCredentialRepository>().Get());
    }

    private void DescribeJev(JevCredential? credential)
    {
        var provider = JevProvider.FromId(credential?.Provider);
        if (credential is null || provider is null)
        {
            _jev.Status.Text = "No Jev key saved.";
            _jev.Status.ForeColor = Theme.TextSecondary;
            return;
        }

        _jevProvider.SelectedItem = provider;
        var tail = credential.Token.Length >= 6 ? credential.Token[^6..] : "……";
        _jev.Status.Text = $"Saved for {provider.DisplayName} (ends …{tail}).";
        _jev.Status.ForeColor = Theme.TextSecondary;
    }

    private JevProvider SelectedJevProvider =>
        _jevProvider.SelectedItem as JevProvider ?? JevProvider.OpenRouter;

    /// <summary>
    /// Stores the key only after Jev has actually answered with it, so a key saved here is known
    /// to reach the model - not merely to be accepted by the provider's front door.
    /// </summary>
    private async Task SaveJevAsync()
    {
        var token = _jev.Token.Text.Trim();
        if (token.Length == 0) { Fail(_jev, "Paste a key first."); return; }

        var provider = SelectedJevProvider;
        try
        {
            var result = await _sp.GetRequiredService<IJevClient>()
                .CheckAsync(new JevCredential(provider.Id, token), CancellationToken.None);

            _sp.GetRequiredService<JevCredentialRepository>().Save(provider.Id, token);
            var cost = result.Usage.CostUsd is { } usd
                ? $" The test cost ${usd.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}."
                : "";
            Stored(_jev, token, $"Jev answered through {provider.DisplayName} ({result.Model}).{cost}");
        }
        catch (JevException ex)
        {
            _log.LogWarning("Jev key test via {Provider} failed: {Message}", provider.DisplayName, ex.Message);
            Fail(_jev, ex.StatusCode switch
            {
                401 or 403 => $"{provider.DisplayName} refused that key. Check it was issued by "
                            + $"{provider.DisplayName} - a key from the other provider is refused.",
                402 => $"{provider.DisplayName} accepted the key but the account has no credit left.",
                _ => ex.Message
            });
        }
    }

    private void OpenJevKeyPage() =>
        OpenUrl(SelectedJevProvider.KeyPageUrl, SelectedJevProvider.DisplayName);

    private static void Describe(TokenSection section, AtlassianCredential? credential, string which)
    {
        if (credential is null)
        {
            section.Status.Text = $"No {which} token saved.";
            section.Status.ForeColor = Theme.TextSecondary;
            return;
        }

        // Never the whole token, and never enough of it to be useful if this window is screenshared.
        var tail = credential.Token.Length >= 6 ? credential.Token[^6..] : "……";
        section.Status.Text = $"Saved for {credential.Email} (ends …{tail}).";
        section.Status.ForeColor = Theme.TextSecondary;
    }

    private async Task SaveJiraAsync()
    {
        var email = _emailBox.Text.Trim();
        var token = _jira.Token.Text.Trim();
        if (!Validate(_jira, email, token)) return;

        var site = await _sp.GetRequiredService<IJiraSiteResolver>()
                            .GetSiteUrlAsync(CancellationToken.None);
        if (site is null)
        {
            Fail(_jira, "No Jira site known yet — connect on the Connection tab first.");
            return;
        }

        var (ok, detail) = await ProbeAsync($"{site.TrimEnd('/')}/rest/api/3/myself", email, token);
        if (!ok) { Fail(_jira, detail); return; }

        _sp.GetRequiredService<JiraApiTokenRepository>().Save(email, token);
        Stored(_jira, token, "Jira accepted the token.");
    }

    private async Task SaveBitbucketAsync()
    {
        var email = _emailBox.Text.Trim();
        var token = _bitbucket.Token.Text.Trim();
        if (!Validate(_bitbucket, email, token)) return;

        var (ok, detail) = await ProbeAsync("https://api.bitbucket.org/2.0/user", email, token);
        if (!ok)
        {
            // The single most likely mistake, and the API says so in as many words.
            if (detail.Contains("no Bitbucket scopes", StringComparison.OrdinalIgnoreCase))
                detail = "That token has no Bitbucket scopes — it is probably the Jira one. "
                       + "Use \"Create API token with scopes\" and tick read:pullrequest:bitbucket.";
            Fail(_bitbucket, detail);
            return;
        }

        _sp.GetRequiredService<BitbucketApiTokenRepository>().Save(email, token);
        Stored(_bitbucket, token, "Bitbucket accepted the token.");
    }

    /// <summary>
    /// GitLab needs no email - the token alone authenticates - so this skips the email check
    /// the two Atlassian sections share, and stores the username the probe reports instead.
    /// </summary>
    private async Task SaveGitLabAsync()
    {
        var token = _gitlab.Token.Text.Trim();
        if (token.Length == 0) { Fail(_gitlab, "Paste a token first."); return; }

        var (ok, detail) = await ProbeGitLabAsync(token);
        if (!ok)
        {
            // The most likely mistake by far: a token minted for git access, or a
            // fine-grained one without read_api. GitLab names the reason in the body.
            if (detail.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase))
                detail = "That token reached GitLab but carries the wrong scope - it needs "
                       + "read_api. A token created for git access will not work here.";
            Fail(_gitlab, detail);
            return;
        }

        _sp.GetRequiredService<GitLabApiTokenRepository>().Save(detail, token);
        Stored(_gitlab, token, $"GitLab accepted the token for {detail}.");
    }

    /// <summary>Returns the GitLab username on success, so the caller can store it.</summary>
    private async Task<(bool Ok, string Detail)> ProbeGitLabAsync(string token)
    {
        try
        {
            using var http = _sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://gitlab.com/api/v4/user");
            request.Headers.Add("PRIVATE-TOKEN", token);

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, $"{(int)response.StatusCode}: {Trim(body)}");

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var username = doc.RootElement.TryGetProperty("username", out var u)
                ? u.GetString()
                : null;

            return (true, string.IsNullOrWhiteSpace(username) ? "your GitLab account" : username);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitLab token test failed");
            return (false, ex.Message);
        }
    }

    private void OpenGitLabTokenPage() => OpenUrl(GitLabTokenPageUrl, "GitLab");

    private bool Validate(TokenSection section, string email, string token)
    {
        if (email.Length == 0) { Fail(section, "Enter your Atlassian account email first."); return false; }
        if (token.Length == 0) { Fail(section, "Paste a token first."); return false; }
        return true;
    }

    private async Task<(bool Ok, string Detail)> ProbeAsync(string url, string email, string token)
    {
        try
        {
            using var http = _sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));

            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return (true, string.Empty);

            var body = await response.Content.ReadAsStringAsync();
            return (false, $"{(int)response.StatusCode}: {Trim(body)}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Token test against {Url} failed", url);
            return (false, ex.Message);
        }
    }

    private static string Trim(string body) =>
        body.Length <= 180 ? body : body[..180] + "…";

    private static void Fail(TokenSection section, string message)
    {
        section.Status.Text = message;
        section.Status.ForeColor = Theme.Danger;
    }

    private static void Succeed(TokenSection section, string message)
    {
        section.Status.Text = message;
        section.Status.ForeColor = Theme.Accent;
    }

    /// <summary>
    /// Confirms a save without leaving the token on screen.
    ///
    /// The field is emptied on purpose, but that alone looks identical to losing the input - the
    /// status line already read "Saved for ..." from the previous token, so nothing appeared to
    /// happen. The time and the new token's tail are what make the change visible, and the message
    /// says the clearing was deliberate.
    /// </summary>
    private static void Stored(TokenSection section, string token, string message)
    {
        section.Token.Clear();
        var tail = token.Length >= 6 ? token[^6..] : "……";
        Succeed(section,
            $"✓ {DateTime.Now:HH:mm} — {message} Saved (ends …{tail}); the box is cleared so the "
            + "token is not left on screen.");
    }

    private void OpenTokenPage() => OpenUrl(TokenPageUrl, "Atlassian");

    private void OpenUrl(string url, string which)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the {Which} token page", which);
        }
    }

    /// <summary>
    /// One token's worth of UI. The walkthrough is hidden until the user presses the button that
    /// sends them to Atlassian, so the page stays readable but the steps are on screen at exactly
    /// the moment they are on the other site trying to follow them.
    /// </summary>
    private sealed class TokenSection
    {
        public TextBox Token { get; }
        public FlatButton Save { get; }
        public Label Status { get; }
        public IReadOnlyList<Control> Controls { get; }

        public TokenSection(string title, string hint, string[] walkthrough, Action onCreate)
        {
            Token = new TextBox
            {
                Font = Theme.Mono,
                BackColor = Theme.Surface,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = true,
                Width = 380,
                Margin = new Padding(0, 0, 8, 0)
            };

            // No ampersands in button text: FlatButton draws with TextRenderer.DrawText without
            // NoPrefix, so "&" is eaten as a mnemonic.
            var create = new FlatButton { Text = "Get a token", Width = 110 };
            Save = new FlatButton { Text = "Save and test", Width = 120, Kind = ButtonKind.Accent };

            Status = new Label
            {
                Text = "",
                Font = Theme.Body,
                ForeColor = Theme.TextSecondary,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 10)
            };

            var steps = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Visible = false,
                BackColor = Theme.Surface,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(16, 4, 16, 4)
            };
            foreach (var line in walkthrough)
            {
                steps.Controls.Add(new Label
                {
                    Text = line,
                    Font = line.Length == 0 ? Theme.Body : Theme.Mono,
                    ForeColor = Theme.TextPrimary,
                    AutoSize = true,
                    MaximumSize = new Size(520, 0),
                    Margin = new Padding(0, 1, 0, 1)
                });
            }

            create.Click += (_, _) =>
            {
                steps.Visible = true;
                onCreate();
            };

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(16, 4, 16, 0)
            };
            row.Controls.Add(Token);
            row.Controls.Add(create);
            row.Controls.Add(Save);

            // Bottom-up: the caller adds these in order, and docked children stack in reverse.
            Controls = new Control[]
            {
                Status,
                steps,
                row,
                MakeHint(hint),
                MakeHeading(title)
            };
        }
    }

    private static Label MakeHint(string text) => new()
    {
        Text = text,
        Font = Theme.Body,
        ForeColor = Theme.TextSecondary,
        Dock = DockStyle.Top,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Padding = new Padding(16, 0, 16, 4)
    };

    private static Label MakeHeading(string text) => new()
    {
        Text = text,
        Font = Theme.Heading,
        ForeColor = Theme.TextPrimary,
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(16, 12, 16, 6)
    };
}
