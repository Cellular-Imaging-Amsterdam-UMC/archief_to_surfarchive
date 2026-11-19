namespace SurfArchive;

internal sealed class ArchiveQueuePanel : UserControl
{
    public const int EmptyHeight = 43;
    private const int RowHeight = 28;
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private readonly TableLayoutPanel rows = new()
    {
        Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty
    };
    private readonly Label summary = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly ToolTip tips = new();

    public ArchiveQueuePanel()
    {
        AccessibleName = "Archive queue";
        BackColor = DarkTheme.Panel;
        Dock = DockStyle.Fill;
        Padding = new Padding(12, 4, 12, 4);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 26));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = DarkTheme.Label("Queue", DarkTheme.Heading);
        heading.Margin = new Padding(0, 2, 0, 0);
        summary.Margin = new Padding(12, 5, 0, 0);
        layout.Controls.Add(DarkTheme.Row(heading, summary), 0, 0);
        rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        scroll.Controls.Add(rows);
        DarkTheme.Explorer(scroll);
        layout.Controls.Add(scroll, 0, 1);
        Controls.Add(layout);
        ShowItems([]);
    }

    public event Action<ArchiveQueueItem>? TogglePause;
    public event Action<ArchiveQueueItem>? RemoveItem;

    public static int HeightFor(int count) => count == 0 ? EmptyHeight : 40 + RowHeight * Math.Min(count, 3);

    public void ShowItems(IReadOnlyList<ArchiveQueueItem> items)
    {
        int queued = items.Count(item => item.State == ArchiveQueueState.Queued);
        int paused = items.Count(item => item.State == ArchiveQueueState.Paused);
        summary.Text = items.Count == 0 ? "No archives queued" : string.Join("  ·  ",
            new[] { queued > 0 ? $"{queued:N0} waiting" : "", paused > 0 ? $"{paused:N0} paused" : "",
                items.Any(item => item.State == ArchiveQueueState.Running) ? "1 running" : "" }.Where(text => text.Length > 0));

        int verticalOffset = -scroll.AutoScrollPosition.Y;
        bool focused = ContainsFocus;
        var focusedItem = rows.Controls.OfType<Control>().FirstOrDefault(row => row.ContainsFocus)?.Tag as ArchiveQueueItem;
        rows.SuspendLayout();
        tips.RemoveAll();
        while (rows.Controls.Count > 0)
        {
            var old = rows.Controls[0];
            rows.Controls.RemoveAt(0);
            old.Dispose();
        }
        rows.RowStyles.Clear();
        rows.RowCount = items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            rows.RowStyles.Add(new(SizeType.Absolute, RowHeight));
            rows.Controls.Add(CreateRow(items[i], i), 0, i);
        }
        rows.ResumeLayout();
        scroll.AutoScrollPosition = new Point(0, verticalOffset);
        if (focused && focusedItem is not null &&
            rows.Controls.OfType<Control>().FirstOrDefault(row => ReferenceEquals(row.Tag, focusedItem)) is { } retained)
            retained.SelectNextControl(null, forward: true, tabStopOnly: true, nested: true, wrap: false);
    }

    private Control CreateRow(ArchiveQueueItem item, int index)
    {
        var active = item.State == ArchiveQueueState.Running;
        var row = new TableLayoutPanel
        {
            Tag = item, AccessibleName = $"{item.Request.Name}, {item.State}",
            BackColor = active ? DarkTheme.Selected : index % 2 == 0 ? DarkTheme.Page : DarkTheme.Panel,
            Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(7, 0, 2, 0)
        };
        row.ColumnStyles.Add(new(SizeType.Percent, 38));
        row.ColumnStyles.Add(new(SizeType.Percent, 62));
        row.ColumnStyles.Add(new(SizeType.Absolute, 86));
        row.ColumnStyles.Add(new(SizeType.Absolute, 30));
        row.ColumnStyles.Add(new(SizeType.Absolute, 30));
        AddText(item.Request.Name, DarkTheme.Ink, 0,
            $"Source: {item.Request.Plan.Source}\nDestination: {item.Request.RemotePath}");
        AddText(item.Request.RemotePath, DarkTheme.Muted, 1, item.Request.RemotePath);
        AddText(item.State.ToString(), active ? DarkTheme.Mint : item.State == ArchiveQueueState.Paused ? DarkTheme.Amber : DarkTheme.Muted, 2);
        if (!active)
        {
            bool paused = item.State == ArchiveQueueState.Paused;
            AddButton(paused ? ActionIcon.Play : ActionIcon.Pause, paused ? "Resume" : "Pause", 3, () => TogglePause?.Invoke(item));
            AddButton(ActionIcon.Trash, "Remove from queue", 4, () => RemoveItem?.Invoke(item));
        }
        return row;

        void AddText(string text, Color color, int column, string? tooltip = null)
        {
            var label = DarkTheme.Label(text, DarkTheme.Small, color);
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0, 0, 6, 0);
            label.Dock = DockStyle.Fill;
            row.Controls.Add(label, column, 0);
            if (tooltip is not null)
                tips.SetToolTip(label, tooltip);
        }

        void AddButton(ActionIcon icon, string action, int column, Action click)
        {
            var button = DarkTheme.IconButton(icon, $"{action} {item.Request.Name}");
            button.Click += (_, _) => click();
            tips.SetToolTip(button, button.AccessibleName);
            row.Controls.Add(button, column, 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            tips.Dispose();
        base.Dispose(disposing);
    }
}
