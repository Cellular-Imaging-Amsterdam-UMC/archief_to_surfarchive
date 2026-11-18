using System.Runtime.InteropServices;

namespace SurfArchive;

internal static class DarkTheme
{
    public static readonly Color Page = Color.FromArgb(18, 23, 31);
    public static readonly Color Panel = Color.FromArgb(24, 31, 41);
    public static readonly Color Raised = Color.FromArgb(31, 41, 54);
    public static readonly Color Border = Color.FromArgb(49, 62, 78);
    public static readonly Color Ink = Color.FromArgb(232, 239, 247);
    public static readonly Color Muted = Color.FromArgb(159, 176, 196);
    public static readonly Color Mint = Color.FromArgb(112, 227, 196);
    public static readonly Color Selected = Color.FromArgb(29, 70, 72);
    public static readonly Color Amber = Color.FromArgb(244, 188, 100);
    public static readonly Color Error = Color.FromArgb(255, 144, 145);
    public static readonly Font Body = new("Segoe UI", 10);
    public static readonly Font Small = new("Segoe UI", 9);
    public static readonly Font Heading = new("Segoe UI Semibold", 12);
    public static readonly Font Brand = new("Segoe UI Semibold", 17);
    private static readonly Icon? AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

    public static void Window(Form form)
    {
        form.BackColor = Page;
        form.ForeColor = Ink;
        form.Font = Body;
        form.Icon = AppIcon;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.HandleCreated += (_, _) =>
        {
            int dark = 1;
            // Windows 10/11 use attribute 20 for an immersive dark title bar.
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        };
    }

    public static void Explorer(Control control, string theme = "DarkMode_Explorer")
    {
        control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, theme, null);
    }

    public static TextBox Input(string caption = "") => new()
    {
        BackColor = Page, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
        AccessibleName = caption
    };

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text, Font = font ?? Body, ForeColor = color ?? Ink, AutoSize = true,
        Margin = Padding.Empty
    };

    public static Button Button(string text, bool primary = false) =>
        new DarkButton(primary) { Text = text };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static void DrawIcon(Graphics g, Rectangle box, bool folder)
    {
        if (folder)
        {
            using var fill = new SolidBrush(Amber);
            g.FillRectangle(fill, box.X + 1, box.Y + 4, box.Width / 2, 3);
            g.FillRectangle(fill, box.X + 1, box.Y + 6, box.Width - 2, box.Height - 8);
        }
        else
        {
            using var pen = new Pen(Muted);
            int x = box.X + 4, y = box.Y + 2;
            g.DrawPolygon(pen, [new(x, y), new(x + 6, y), new(x + 9, y + 3),
                new(x + 9, y + 12), new(x, y + 12)]);
            g.DrawLine(pen, x + 2, y + 8, x + 7, y + 8);
        }
    }

    private sealed class DarkButton : Button
    {
        private readonly bool primary;
        private bool hovered;

        public DarkButton(bool primary)
        {
            this.primary = primary;
            AutoSize = true;
            MinimumSize = new Size(0, 31);
            Padding = new Padding(10, 1, 10, 1);
            Margin = new Padding(0, 0, 7, 0);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 1;
            Apply();
        }

        private void Apply()
        {
            BackColor = Enabled && primary ? (hovered ? Color.FromArgb(152, 243, 218) : Mint) : Raised;
            ForeColor = Enabled ? (primary ? Page : Ink) : Muted;
            FlatAppearance.BorderColor = Enabled && primary ? Mint : Border;
            FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(152, 243, 218) : Border;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Apply();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled)
            {
                base.OnPaint(e);
                return;
            }
            e.Graphics.Clear(Raised);
            using var border = new Pen(Border);
            e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Apply();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            Apply();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? subId);
}
