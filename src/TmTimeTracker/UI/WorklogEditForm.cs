namespace TmTimeTracker.UI;

public sealed class WorklogEditForm : Form
{
    public string TicketKey { get; }
    public int ObservedMinutes { get; }
    public int SubmittedMinutes { get; private set; }
    public string Description { get; private set; }
    public bool Submitted { get; private set; }

    private readonly NumericUpDown _minutes;
    private readonly TextBox _description;
    private readonly Label _warn;

    public WorklogEditForm(string ticketKey, int observedMinutes, string initialDescription)
    {
        TicketKey = ticketKey;
        ObservedMinutes = observedMinutes;
        SubmittedMinutes = observedMinutes;
        Description = initialDescription;

        Text = $"Log time on {ticketKey}";
        Width = 600; Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        Controls.Add(new Label { Text = $"Ticket: {ticketKey}", Top = 10, Left = 10, AutoSize = true });
        Controls.Add(new Label { Text = "Minutes:", Top = 40, Left = 10, AutoSize = true });

        _minutes = new NumericUpDown
        {
            Top = 38, Left = 100, Width = 80,
            Minimum = 1, Maximum = observedMinutes, Value = observedMinutes
        };
        _minutes.ValueChanged += (_, _) => UpdateWarning();
        Controls.Add(_minutes);

        Controls.Add(new Label
        { Text = $"(observed {observedMinutes}; you may reduce, not increase)",
          Top = 40, Left = 200, AutoSize = true, ForeColor = SystemColors.GrayText });

        Controls.Add(new Label { Text = "Description:", Top = 70, Left = 10, AutoSize = true });
        _description = new TextBox
        {
            Top = 90, Left = 10, Width = 560, Height = 280,
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            Text = initialDescription
        };
        Controls.Add(_description);

        _warn = new Label { Top = 380, Left = 10, AutoSize = true, ForeColor = Color.DarkRed };
        Controls.Add(_warn);

        var submit = new Button { Text = "Submit", Top = 405, Left = 380, Width = 90 };
        submit.Click += (_, _) =>
        {
            SubmittedMinutes = (int)_minutes.Value;
            Description = _description.Text;
            Submitted = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        var later = new Button { Text = "Edit later", Top = 405, Left = 480, Width = 90 };
        later.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(submit);
        Controls.Add(later);
        AcceptButton = submit;
        CancelButton = later;
    }

    private void UpdateWarning()
    {
        _warn.Text = (int)_minutes.Value < ObservedMinutes
            ? $"Reducing observed time by {ObservedMinutes - (int)_minutes.Value} min."
            : string.Empty;
    }
}
