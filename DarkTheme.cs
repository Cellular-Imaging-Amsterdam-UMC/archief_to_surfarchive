using System.Runtime.InteropServices;

namespace SurfArchive;

internal static class DarkTheme
{
    public static readonly SizeF DesignDpi = new(96, 96);
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

    public static Button Button(string text, ActionIcon icon, bool primary = false) =>
        new DarkButton(icon, primary) { Text = text };

    public static Button IconButton(ActionIcon icon, string name) =>
        new DarkButton(icon, false)
        {
            AccessibleName = name, AutoSize = false, MinimumSize = Size.Empty, Size = new Size(26, 25),
            Padding = Padding.Empty, Margin = new Padding(1, 1, 2, 1), ImageAlign = ContentAlignment.MiddleCenter
        };

    public static void SetIcon(Button button, ActionIcon icon)
    {
        if (button is DarkButton styled)
            styled.SetIcon(icon);
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var row = Row(controls);
        row.AutoSize = true;
        row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row.WrapContents = true;
        return row;
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
        private ActionIcon icon;

        public DarkButton(ActionIcon icon, bool primary)
        {
            this.icon = icon;
            this.primary = primary;
            Font = Small;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(0, 26);
            Padding = new Padding(7, 0, 7, 0);
            Margin = new Padding(0, 0, 6, 0);
            ImageAlign = ContentAlignment.MiddleLeft;
            TextImageRelation = TextImageRelation.ImageBeforeText;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 1;
            Apply();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var size = base.GetPreferredSize(proposedSize);
            if (!AutoSize)
                return size;
            int contentHeight = Math.Max(Font.Height, Image?.Height ?? 0);
            int height = Math.Max(DeviceDpi * 26 / 96, contentHeight + DeviceDpi * 6 / 96);
            return new Size(size.Width, height);
        }

        public void SetIcon(ActionIcon value)
        {
            if (icon == value)
                return;
            icon = value;
            UpdateImage();
        }

        private void UpdateImage() => Image = ActionIcons.Get(icon, ForeColor, DeviceDpi * 16 / 96);

        private void Apply()
        {
            BackColor = Enabled && primary ? (hovered ? Color.FromArgb(152, 243, 218) : Mint) : Raised;
            ForeColor = Enabled ? (primary ? Page : Ink) : Muted;
            FlatAppearance.BorderColor = Enabled && primary ? Mint : Border;
            FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(152, 243, 218) : Border;
            UpdateImage();
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
            var bitmap = Image!;
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int textWidth = Text.Length > 0 ? TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, flags).Width : 0;
            int gap = textWidth > 0 ? 4 : 0;
            int start = (ClientSize.Width - bitmap.Width - gap - textWidth) / 2;
            e.Graphics.DrawImageUnscaled(bitmap, start, (ClientSize.Height - bitmap.Height) / 2);
            if (textWidth > 0)
            {
                int textLeft = start + bitmap.Width + gap;
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(textLeft, 1, ClientSize.Width - textLeft,
                    Math.Max(0, ClientSize.Height - 2)), Muted, flags);
            }
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            UpdateImage();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateImage();
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
