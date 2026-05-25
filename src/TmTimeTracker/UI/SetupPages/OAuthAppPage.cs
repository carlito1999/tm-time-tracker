using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class OAuthAppPage : UserControl
{
    private readonly TextBox _clientId;
    private readonly TextBox _clientSecret;
    private readonly TextBox _redirectUri;
    public event Action? ValidationChanged;

    public OAuthAppPage()
    {
        Dock = DockStyle.Fill;
        var lbl = new Label
        {
            Top = 10, Left = 10, Width = 540, Height = 60, AutoSize = false,
            Text = "Register an OAuth 2.0 (3LO) integration at developer.atlassian.com " +
                   "and paste its Client ID and Secret below. The values are encrypted " +
                   "with Windows DPAPI before being saved to state.db."
        };
        var openLink = new Button { Top = 75, Left = 10, Width = 260, Text = "Open developer.atlassian.com" };
        openLink.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://developer.atlassian.com/console/myapps/",
            UseShellExecute = true
        });

        Controls.Add(lbl);
        Controls.Add(openLink);

        Controls.Add(new Label { Top = 120, Left = 10, AutoSize = true, Text = "Client ID:" });
        _clientId = new TextBox { Top = 140, Left = 10, Width = 540 };
        _clientId.TextChanged += (_, _) => ValidationChanged?.Invoke();
        Controls.Add(_clientId);

        Controls.Add(new Label { Top = 175, Left = 10, AutoSize = true, Text = "Client Secret:" });
        _clientSecret = new TextBox { Top = 195, Left = 10, Width = 540, UseSystemPasswordChar = true };
        _clientSecret.TextChanged += (_, _) => ValidationChanged?.Invoke();
        Controls.Add(_clientSecret);

        Controls.Add(new Label
        {
            Top = 230, Left = 10, AutoSize = true,
            Text = "Redirect URI (configure this exact URL in your Atlassian app):"
        });
        _redirectUri = new TextBox
        {
            Top = 250, Left = 10, Width = 540, ReadOnly = true,
            Text = "http://localhost:53682/callback"
        };
        Controls.Add(_redirectUri);
    }

    public bool IsValid => _clientId.Text.Trim().Length > 0 && _clientSecret.Text.Length > 0;

    public OAuthAppConfig BuildConfig() => new(
        ClientId: _clientId.Text.Trim(),
        ClientSecret: _clientSecret.Text,
        RedirectUri: _redirectUri.Text.Trim());

    public void LoadConfig(OAuthAppConfig? c)
    {
        if (c is null) return;
        _clientId.Text = c.ClientId;
        _clientSecret.Text = c.ClientSecret;
        _redirectUri.Text = c.RedirectUri;
    }
}
