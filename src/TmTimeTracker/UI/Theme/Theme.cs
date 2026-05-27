using System.Runtime.InteropServices;

namespace TmTimeTracker.UI.Theming;

internal static class Theme
{
    public static readonly Color Background     = Color.FromArgb(0x1E, 0x1E, 0x1E);
    public static readonly Color Surface        = Color.FromArgb(0x25, 0x25, 0x26);
    public static readonly Color SurfaceAlt     = Color.FromArgb(0x2D, 0x2D, 0x30);
    public static readonly Color Border         = Color.FromArgb(0x3E, 0x3E, 0x42);
    public static readonly Color TextPrimary    = Color.FromArgb(0xD4, 0xD4, 0xD4);
    public static readonly Color TextSecondary  = Color.FromArgb(0x85, 0x85, 0x85);
    public static readonly Color Accent         = Color.FromArgb(0x4E, 0xC9, 0xB0);
    public static readonly Color AccentMuted    = Color.FromArgb(0x3A, 0x8F, 0x80);
    public static readonly Color Danger         = Color.FromArgb(0xF4, 0x87, 0x71);
    public static readonly Color ClaudeAccent   = Color.FromArgb(0xC5, 0x86, 0xC0);
    public static readonly Color Idle           = Color.FromArgb(0x85, 0x85, 0x85);

    public static readonly Color KindBranch     = Color.FromArgb(0x9C, 0xDC, 0xFE);
    public static readonly Color KindActivity   = Accent;
    public static readonly Color KindJira       = Color.FromArgb(0xDC, 0xDC, 0xAA);
    public static readonly Color KindWorklog    = Accent;
    public static readonly Color KindAuth       = Danger;

    public static readonly Font Body    = new("Segoe UI", 9f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11f);
    public static readonly Font Mono    = new("Consolas", 9f);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = TextPrimary;
        form.Font      = Body;
        TrySetDarkTitleBar(form);
        ApplyToChildren(form);
    }

    private static void ApplyToChildren(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case FlatButton:        break;
                case StatusPill:        break;
                case DataGridView grid: StyleDataGridView(grid); break;
                case ListView list:     StyleListView(list);     break;
                case ComboBox combo:    StyleComboBox(combo);    break;
                case ListBox lb:        StyleListBox(lb);        break;
                case RadioButton rb:    StyleRadioButton(rb);    break;
                case CheckBox cb:       StyleCheckBox(cb);       break;
                case StatusStrip strip: StyleStatusStrip(strip); break;
                case TextBox tb:        StyleTextBox(tb);        break;
                case NumericUpDown nud: StyleNumericUpDown(nud); break;
                case Button btn:        StyleFallbackButton(btn); break;
                case Label lbl:         StyleLabel(lbl);         break;
                case Panel:
                    c.BackColor = Background;
                    c.ForeColor = TextPrimary;
                    break;
            }
            if (c.HasChildren) ApplyToChildren(c);
        }
    }

    private static void StyleLabel(Label l)
    {
        if (l.ForeColor == SystemColors.ControlText) l.ForeColor = TextPrimary;
        else if (l.ForeColor == SystemColors.GrayText) l.ForeColor = TextSecondary;
        l.BackColor = Color.Transparent;
    }

    private static void StyleDataGridView(DataGridView g)
    {
        g.BackgroundColor = Surface;
        g.GridColor = Border;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceAlt,
            ForeColor = TextPrimary,
            Font = Heading,
            SelectionBackColor = SurfaceAlt,
            SelectionForeColor = TextPrimary,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 6, 0)
        };
        g.ColumnHeadersHeight = 32;
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = TextPrimary,
            SelectionBackColor = AccentMuted,
            SelectionForeColor = Background,
            Padding = new Padding(6, 0, 6, 0),
            Font = Body
        };
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceAlt,
            ForeColor = TextPrimary,
            SelectionBackColor = AccentMuted,
            SelectionForeColor = Background,
            Font = Body
        };
        g.RowsDefaultCellStyle.BackColor = Surface;
        g.RowsDefaultCellStyle.SelectionBackColor = AccentMuted;
        g.RowsDefaultCellStyle.SelectionForeColor = Background;
        g.RowTemplate.Height = 28;
    }

    private static void StyleListView(ListView l)
    {
        l.BackColor = Surface;
        l.ForeColor = TextPrimary;
        l.BorderStyle = BorderStyle.None;
        l.OwnerDraw = true;
        l.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(SurfaceAlt);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Border);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(
                e.Graphics, e.Header!.Text, Heading,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        l.DrawItem += (_, _) => { };
        l.DrawSubItem += (_, e) =>
        {
            var bgColor = e.ItemIndex % 2 == 1 ? SurfaceAlt : Surface;
            using var bg = new SolidBrush(bgColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var color = e.ColumnIndex == 1 ? KindForName(e.SubItem!.Text) : TextPrimary;
            var font  = e.ColumnIndex == 0 ? Mono : Body;
            TextRenderer.DrawText(
                e.Graphics, e.SubItem!.Text, font,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
    }

    private static Color KindForName(string kind) => kind switch
    {
        "BranchChanged"          => KindBranch,
        "ActivityChanged"        => KindActivity,
        "JiraStatusTransition"   => KindJira,
        "WorklogSubmitted"       => KindWorklog,
        "AuthenticationRequired" => KindAuth,
        _ => TextSecondary
    };

    private static void StyleComboBox(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Surface;
        c.ForeColor = TextPrimary;
    }

    private static void StyleListBox(ListBox l)
    {
        l.BackColor = Surface;
        l.ForeColor = TextPrimary;
        l.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleRadioButton(RadioButton r)
    {
        r.BackColor = Color.Transparent;
        r.ForeColor = TextPrimary;
        r.FlatStyle = FlatStyle.Flat;
        r.FlatAppearance.BorderColor = Border;
        r.FlatAppearance.MouseOverBackColor = SurfaceAlt;
    }

    private static void StyleCheckBox(CheckBox c)
    {
        c.BackColor = Color.Transparent;
        c.ForeColor = TextPrimary;
        c.FlatStyle = FlatStyle.Flat;
        c.FlatAppearance.BorderColor = Border;
        c.FlatAppearance.MouseOverBackColor = SurfaceAlt;
    }

    private static void StyleStatusStrip(StatusStrip s)
    {
        s.BackColor = SurfaceAlt;
        s.ForeColor = TextSecondary;
        s.SizingGrip = false;
        s.RenderMode = ToolStripRenderMode.System;
        foreach (ToolStripItem item in s.Items)
        {
            item.BackColor = SurfaceAlt;
            item.ForeColor = TextSecondary;
            item.Font = Body;
        }
    }

    private static void StyleTextBox(TextBox t)
    {
        t.BackColor = Surface;
        t.ForeColor = TextPrimary;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleNumericUpDown(NumericUpDown n)
    {
        n.BackColor = Surface;
        n.ForeColor = TextPrimary;
        n.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleFallbackButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Surface;
        b.ForeColor = TextPrimary;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = SurfaceAlt;
        b.FlatAppearance.MouseDownBackColor = AccentMuted;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static void TrySetDarkTitleBar(Form form)
    {
        try
        {
            int enabled = 1;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch
        {
        }
    }
}
