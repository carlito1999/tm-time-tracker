using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Services;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI.SettingsPages;

/// <summary>
/// The two Atlassian API tokens, side by side.
///
/// They come from the same page and look identical, but are not interchangeable - the Jira one is
/// scopeless, and Bitbucket rejects it outright. Showing them together, each with its own
/// walkthrough naming the exact button and scope, is the point of this page: the difference is
/// invisible once a token is pasted, and diagnosing it afterwards means reading a 401 body.
/// </summary>
public sealed class ApiTokensPage : UserControl
{
    private const string TokenPageUrl = "https://id.atlassian.com/manage-profile/security/api-tokens";

    private readonly IServiceProvider _sp;
    private readonly ILogger _log;
    private readonly TextBox _emailBox;
    private readonly TokenSection _jira;
    private readonly TokenSection _bitbucket;

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

        _jira.Save.Click += async (_, _) => await SaveJiraAsync();
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

        // Docked children stack in reverse order of addition, so add bottom-up.
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
    }

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
        _jira.Token.Clear();
        Succeed(_jira, "Saved. Jira accepted the token.");
        Initialise();
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
        _bitbucket.Token.Clear();
        Succeed(_bitbucket, "Saved. Bitbucket accepted the token.");
        Initialise();
    }

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

    private void OpenTokenPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = TokenPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the Atlassian token page");
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
