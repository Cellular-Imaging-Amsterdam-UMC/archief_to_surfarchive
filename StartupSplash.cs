namespace SurfArchive;

internal sealed class StartupSplash : Form
{
    public StartupSplash()
    {
        DarkTheme.Window(this);
        Text = "Starting SurfDataArchiveTransfer";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(460, 154);
        BackColor = DarkTheme.Border;
        Padding = new Padding(1);
        DoubleBuffered = true;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DarkTheme.Panel, RowCount = 3, ColumnCount = 2,
            Padding = new Padding(24, 25, 24, 24)
        };
        content.ColumnStyles.Add(new(SizeType.Absolute, 62));
        content.ColumnStyles.Add(new(SizeType.Percent, 100));
        content.RowStyles.Add(new(SizeType.Percent, 70));
        content.RowStyles.Add(new(SizeType.Percent, 30));
        content.RowStyles.Add(new(SizeType.Absolute, 11));
        var symbol = DarkTheme.Label("▣", DarkTheme.Brand, DarkTheme.Mint);
        symbol.BackColor = DarkTheme.Selected;
        symbol.TextAlign = ContentAlignment.MiddleCenter;
        symbol.AutoSize = false;
        symbol.Size = new Size(45, 45);
        symbol.Anchor = AnchorStyles.Left;
        content.Controls.Add(symbol, 0, 0);
        content.SetRowSpan(symbol, 2);
        var title = DarkTheme.Label("SurfDataArchiveTransfer", DarkTheme.Brand);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        content.Controls.Add(title, 1, 0);
        var status = DarkTheme.Label("Starting…", DarkTheme.Small, DarkTheme.Muted);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        status.Margin = new Padding(1, 4, 0, 0);
        content.Controls.Add(status, 1, 1);
        var accent = new Panel { BackColor = DarkTheme.Mint, Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Size = new Size(44, 2), Margin = Padding.Empty };
        content.Controls.Add(accent, 1, 2);
        Controls.Add(content);
    }

    protected override bool ShowWithoutActivation => true;
}
