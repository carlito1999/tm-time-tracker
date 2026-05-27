using TmTimeTracker.UI.Theming;

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
        Icon = AppIcon.Load();
        ClientSize = new Size(620, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 16, 16, 4)
        };
        var titleLabel = new Label
        {
            Text = "Log time on",
            Font = Theme.Heading,
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 0)
        };
        var ticketPill = new StatusPill { ShowDot = false, Font = Theme.Mono };
        ticketPill.Set(ticketKey, PillTone.Active);
        header.Controls.Add(titleLabel);
        header.Controls.Add(ticketPill);

        var subtitle = new Label
        {
            Text = $"Observed {observedMinutes} min in this cycle. You can reduce but not increase.",
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            AutoSize = true,
            Padding = new Padding(16, 0, 16, 12)
        };

        var minutesRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 8)
        };
        minutesRow.Controls.Add(new Label
        {
            Text = "Minutes",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0)
        });
        _minutes = new NumericUpDown
        {
            Width = 90,
            Minimum = 1,
            Maximum = observedMinutes,
            Value = observedMinutes,
            Font = Theme.Mono
        };
        _minutes.ValueChanged += (_, _) => UpdateWarning();
        minutesRow.Controls.Add(_minutes);

        var descLabel = new Label
        {
            Text = "Description",
            Font = Theme.Heading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Padding = new Padding(16, 8, 16, 4)
        };

        _description = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = initialDescription,
            Font = Theme.Mono,
            Margin = new Padding(16, 0, 16, 8)
        };
        var descCell = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 0, 16, 8)
        };
        descCell.Controls.Add(_description);

        _warn = new Label
        {
            ForeColor = Theme.Danger,
            Font = Theme.Body,
            AutoSize = true,
            Padding = new Padding(16, 0, 16, 4),
            Visible = false
        };

        var submit = new FlatButton { Text = "▶  Submit", Kind = ButtonKind.Accent, Width = 130 };
        var later  = new FlatButton { Text = "Edit later", Width = 110 };
        submit.Click += (_, _) =>
        {
            SubmittedMinutes = (int)_minutes.Value;
            Description = _description.Text;
            Submitted = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        later.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var buttonsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 16)
        };
        submit.Margin = new Padding(8, 0, 0, 0);
        later.Margin  = new Padding(8, 0, 0, 0);
        buttonsRow.Controls.Add(submit);
        buttonsRow.Controls.Add(later);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // subtitle
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // minutes row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // description label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // description box
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // warning
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons
        root.Controls.Add(header,     0, 0);
        root.Controls.Add(subtitle,   0, 1);
        root.Controls.Add(minutesRow, 0, 2);
        root.Controls.Add(descLabel,  0, 3);
        root.Controls.Add(descCell,   0, 4);
        root.Controls.Add(_warn,      0, 5);
        root.Controls.Add(buttonsRow, 0, 6);
        Controls.Add(root);

        AcceptButton = submit;
        CancelButton = later;

        Theme.Apply(this);
    }

    private void UpdateWarning()
    {
        if ((int)_minutes.Value < ObservedMinutes)
        {
            _warn.Text = $"Reducing observed time by {ObservedMinutes - (int)_minutes.Value} min.";
            _warn.Visible = true;
        }
        else
        {
            _warn.Visible = false;
        }
    }
}
