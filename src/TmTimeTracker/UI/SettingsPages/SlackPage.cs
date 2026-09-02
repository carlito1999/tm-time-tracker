using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Slack;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI.SettingsPages;

/// <summary>
/// Slack connection plus one card per Jira project: which channel to post to and what to say.
/// The project list and the channel suggestions are derived, so setup is confirm-not-configure.
/// </summary>
public sealed class SlackPage : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly ILogger _log;

    private readonly TextBox _tokenBox;
    private readonly FlatButton _saveTokenBtn;
    private readonly Label _connectionStatus;
    private readonly FlowLayoutPanel _projectCards;
    private readonly Label _projectsEmpty;

    private IReadOnlyList<SlackConversation> _channels = Array.Empty<SlackConversation>();

    public SlackPage(IServiceProvider sp, ILogger log)
    {
        _sp = sp;
        _log = log;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        AutoScroll = true;

        _tokenBox = new TextBox
        {
            Font = Theme.Mono,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true,
            Width = 380,
            Margin = new Padding(0, 0, 8, 0)
        };

        _saveTokenBtn = new FlatButton { Text = "Save & test", Width = 120, Kind = ButtonKind.Accent };
        _saveTokenBtn.Click += async (_, _) => await SaveAndTestTokenAsync();

        _connectionStatus = new Label
        {
            Text = "Not connected",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Padding = new Padding(16, 6, 16, 12)
        };

        var tokenRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 0)
        };
        tokenRow.Controls.Add(_tokenBox);
        tokenRow.Controls.Add(_saveTokenBtn);

        _projectsEmpty = new Label
        {
            Text = "Connect Slack to choose channels.",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Padding = new Padding(16, 4, 16, 8)
        };

        _projectCards = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12, 0, 12, 8)
        };

        // Docked children stack in reverse order of addition, so add bottom-up.
        Controls.Add(_projectCards);
        Controls.Add(_projectsEmpty);
        Controls.Add(MakeHeading("Project channels"));
        Controls.Add(BuildVariablesPanel());
        Controls.Add(_connectionStatus);
        Controls.Add(tokenRow);
        Controls.Add(MakeHeading("Slack connection"));

        Load += async (_, _) => await InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        var credentials = _sp.GetRequiredService<SlackCredentialRepository>();
        if (string.IsNullOrWhiteSpace(credentials.Get()))
        {
            SetStatus("Not connected - paste a Slack user token (xoxp-) above.", Theme.TextSecondary);
            return;
        }

        _tokenBox.Text = new string('•', 24);
        await RefreshConnectionAsync();
    }

    private async Task SaveAndTestTokenAsync()
    {
        var token = _tokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token) || token.All(c => c == '•'))
        {
            SetStatus("Paste a token first.", Theme.Danger);
            return;
        }

        _sp.GetRequiredService<SlackCredentialRepository>().Save(token);
        await RefreshConnectionAsync();
    }

    private async Task RefreshConnectionAsync()
    {
        _saveTokenBtn.Enabled = false;
        SetStatus("Checking...", Theme.TextSecondary);
        try
        {
            var slack = _sp.GetRequiredService<SlackApiClient>();
            var identity = await slack.AuthTestAsync(CancellationToken.None);
            _channels = await slack.ListConversationsAsync(CancellationToken.None);

            SetStatus($"Connected as @{identity.UserName} ({identity.TeamName}) - " +
                      $"{_channels.Count} channels available", Theme.Accent);
            _tokenBox.Text = new string('•', 24);

            await BuildProjectCardsAsync();
        }
        catch (SlackApiException ex)
        {
            SetStatus($"Slack rejected the token: {ex.SlackError}", Theme.Danger);
            _log.LogWarning(ex, "Slack auth.test failed");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not reach Slack: {ex.Message}", Theme.Danger);
            _log.LogWarning(ex, "Slack connection check failed");
        }
        finally
        {
            _saveTokenBtn.Enabled = true;
        }
    }

    private async Task BuildProjectCardsAsync()
    {
        var channelRepo = _sp.GetRequiredService<SlackChannelRepository>();
        var tickets = _sp.GetRequiredService<TicketTimeRepository>();

        // Derived, never typed: every project ever tracked, plus any already configured.
        var projects = tickets.GetDistinctTicketKeys()
            .Select(JiraProjectKey.From)
            .Where(p => p is not null)
            .Concat(channelRepo.GetAll().Select(m => m.ProjectKey)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var projectNames = await LoadProjectNamesAsync();

        _projectCards.Controls.Clear();
        if (projects.Count == 0)
        {
            _projectsEmpty.Text = "No tracked tickets yet - projects appear here once you track time.";
            _projectsEmpty.Visible = true;
            return;
        }

        _projectsEmpty.Visible = false;
        foreach (var project in projects)
            _projectCards.Controls.Add(BuildProjectCard(project!, projectNames, channelRepo));
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadProjectNamesAsync()
    {
        try
        {
            var jira = _sp.GetRequiredService<JiraApiClient>();
            var projects = await jira.ListProjectsAsync(CancellationToken.None);
            return projects.ToDictionary(p => p.Key, p => p.Name, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Only costs the channel suggestion; the user can still pick manually.
            _log.LogDebug(ex, "Could not load Jira project names for channel suggestions");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private Control BuildProjectCard(string project,
        IReadOnlyDictionary<string, string> projectNames, SlackChannelRepository repo)
    {
        var saved = repo.Find(project);

        var card = new Panel
        {
            Width = 640,
            Height = 208,
            BackColor = Theme.Surface,
            Margin = new Padding(4, 4, 4, 8),
            Padding = new Padding(12)
        };

        var title = new Label
        {
            Text = projectNames.TryGetValue(project, out var name) ? $"{project} - {name}" : project,
            Font = Theme.Heading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new Point(12, 10)
        };

        var channelBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Body,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Width = 240,
            Location = new Point(12, 40)
        };
        channelBox.Items.Add("(no channel - do not notify)");
        foreach (var channel in _channels) channelBox.Items.Add("#" + channel.Name);

        var selected = saved is null
            ? ChannelSuggester.Suggest(
                projectNames.TryGetValue(project, out var n) ? n : project,
                _channels.Select(c => c.Name))
            : saved.ChannelName;
        channelBox.SelectedIndex = selected is null
            ? 0
            : Math.Max(0, channelBox.Items.IndexOf("#" + selected));

        var suggestionHint = new Label
        {
            Text = saved is null && selected is not null ? "suggested - confirm to save" : "",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(264, 44)
        };

        var templateBox = new TextBox
        {
            Multiline = true,
            Font = Theme.Mono,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 600,
            Height = 62,
            Location = new Point(12, 74),
            Text = saved?.MessageTemplate ?? SlackVariables.DefaultTemplate
        };

        var preview = new Label
        {
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            Width = 470,
            Height = 52,
            Location = new Point(12, 142)
        };

        var testBtn = new FlatButton
        {
            Text = "Send test message",
            Width = 140,
            Location = new Point(490, 146)
        };

        void UpdatePreview() =>
            preview.Text = MessageTemplateRenderer.Render(templateBox.Text, SlackVariables.Sample());

        string? SelectedChannelId() =>
            channelBox.SelectedIndex <= 0
                ? null
                : _channels[channelBox.SelectedIndex - 1].Id;

        void Save()
        {
            var index = channelBox.SelectedIndex;
            if (index <= 0)
            {
                repo.Remove(project);
                suggestionHint.Text = "";
                return;
            }

            var channel = _channels[index - 1];
            repo.Upsert(new SlackChannelMapping(project, channel.Id, channel.Name, templateBox.Text));
            suggestionHint.Text = "saved";
        }

        templateBox.TextChanged += (_, _) => UpdatePreview();
        templateBox.Leave += (_, _) => Save();
        channelBox.SelectedIndexChanged += (_, _) => Save();

        testBtn.Click += async (_, _) =>
        {
            var channelId = SelectedChannelId();
            if (channelId is null)
            {
                suggestionHint.Text = "pick a channel first";
                return;
            }

            testBtn.Enabled = false;
            try
            {
                // Posts to the real channel where teammates will see it, so it must be
                // unmistakably a test rather than a plausible "Done" someone might act on.
                var body = MessageTemplateRenderer.Render(templateBox.Text, SlackVariables.Sample());
                await _sp.GetRequiredService<SlackApiClient>().PostMessageAsync(
                    channelId,
                    "\U0001F9EA Test from TmTimeTracker - sample values, no action needed\n" + body,
                    CancellationToken.None);
                suggestionHint.Text = "test sent";
            }
            catch (Exception ex)
            {
                suggestionHint.Text = ex is SlackApiException slack ? slack.SlackError : "send failed";
                _log.LogWarning(ex, "Slack test message failed for {Project}", project);
            }
            finally
            {
                testBtn.Enabled = true;
            }
        };

        UpdatePreview();

        card.Controls.AddRange(new Control[]
        {
            title, channelBox, suggestionHint, templateBox, preview, testBtn
        });
        return card;
    }

    private Control BuildVariablesPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16, 0, 16, 8)
        };

        panel.Controls.Add(new Label
        {
            Text = "Available variables (unknown ones are left as-is, so typos are visible):",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4)
        });

        // Rendered from the same catalog the renderer consumes, so the two cannot drift.
        foreach (var variable in SlackVariables.Catalog)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            row.Controls.Add(new Label
            {
                Text = "{" + variable.Name + "}",
                Font = Theme.Mono,
                ForeColor = Theme.Accent,
                AutoSize = true,
                Width = 100
            });
            row.Controls.Add(new Label
            {
                Text = $"{variable.Description}  e.g. {variable.Sample}",
                Font = Theme.Body,
                ForeColor = Theme.TextSecondary,
                AutoSize = true
            });
            panel.Controls.Add(row);
        }

        return panel;
    }

    private void SetStatus(string text, Color color)
    {
        _connectionStatus.Text = text;
        _connectionStatus.ForeColor = color;
    }

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
