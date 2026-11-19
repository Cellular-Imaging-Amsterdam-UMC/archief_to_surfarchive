namespace SurfArchive;

internal sealed class ArchiveQueuePanel : UserControl
{
    public const int EmptyHeight = 52;
    private const int PauseColumn = 3;
    private const int RemoveColumn = 4;
    private readonly ActivityGrid jobs = new() { AccessibleName = "Archive queue items" };
    private readonly Label summary = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly Button removeFinished = DarkTheme.Button("Remove all finished", ActionIcon.Trash);

    public ArchiveQueuePanel()
    {
        AccessibleName = "Archive queue";
        BackColor = DarkTheme.Panel;
        Dock = DockStyle.Fill;
        Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 32));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new(SizeType.AutoSize));
        summary.Margin = new Padding(14, 4, 0, 0);
        heading.Controls.Add(DarkTheme.Row(DarkTheme.Label("↔", DarkTheme.Heading, DarkTheme.Mint),
            DarkTheme.Label("  Queue", DarkTheme.Heading), summary), 0, 0);
        heading.Controls.Add(removeFinished, 1, 0);
        removeFinished.Click += (_, _) => RemoveFinished?.Invoke();
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(jobs, 0, 1);
        Controls.Add(layout);

        jobs.AddTextColumn("Archive", 210, fill: true);
        jobs.AddTextColumn("SURF destination", 280, fill: true);
        jobs.AddTextColumn("Status", 121);
        AddActionColumn();
        AddActionColumn();
        jobs.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                InvokeAction(e.RowIndex, e.ColumnIndex);
        };
        jobs.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && jobs.CurrentCell is { } cell &&
                InvokeAction(cell.RowIndex, cell.ColumnIndex))
                e.Handled = e.SuppressKeyPress = true;
        };
        ShowItems([]);
    }

    public event Action<ArchiveQueueItem>? TogglePause;
    public event Action<ArchiveQueueItem>? RemoveItem;
    public event Action? RemoveFinished;

    public static int HeightFor(int count) => count == 0 ? EmptyHeight
        : EmptyHeight + ActivityGrid.HeaderHeight + ActivityGrid.ItemHeight * Math.Min(count, 3) + 8;

    public void ShowItems(IReadOnlyList<ArchiveQueueItem> items)
    {
        var counts = items.GroupBy(item => item.State).ToDictionary(group => group.Key, group => group.Count());
        summary.Text = items.Count == 0 ? "No archives queued" : string.Join("  ·  ", counts.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Value:N0} {(pair.Key == ArchiveQueueState.Queued ? "waiting" : pair.Key.ToString().ToLowerInvariant())}"));
        removeFinished.Enabled = counts.ContainsKey(ArchiveQueueState.Finished);
        jobs.Visible = items.Count > 0;

        string? selectedId = (jobs.CurrentRow?.Tag as ArchiveQueueItem)?.LocalName;
        int column = jobs.CurrentCell?.ColumnIndex ?? 0;
        int top = jobs.FirstDisplayedScrollingRowIndex;
        DataGridViewRow? selection = null;
        jobs.SuspendLayout();
        jobs.Rows.Clear();
        foreach (var item in items)
        {
            var row = jobs.Rows[jobs.Rows.Add(item.Name, item.RemotePath, item.State.ToString())];
            row.Tag = item;
            if (item.LocalName == selectedId)
                selection = row;
            row.Cells[0].ToolTipText = item.Source;
            row.Cells[1].ToolTipText = item.RemotePath;
            row.Cells[2].ToolTipText = string.Join(Environment.NewLine,
                new[] { item.Detail, item.RecoveryPath is { } path && item.State is ArchiveQueueState.Interrupted
                    or ArchiveQueueState.Failed or ArchiveQueueState.Cancelled ? $"Local package: {path}" : null }
                    .OfType<string>());
            row.Cells[2].Style.ForeColor = item.State switch
            {
                ArchiveQueueState.Running or ArchiveQueueState.Finished => DarkTheme.Mint,
                ArchiveQueueState.Paused or ArchiveQueueState.Interrupted or ArchiveQueueState.Cancelled => DarkTheme.Amber,
                ArchiveQueueState.Failed => DarkTheme.Error,
                _ => DarkTheme.Ink
            };
            if (item.State == ArchiveQueueState.Queued)
                SetAction(row, PauseColumn, ActionIcon.Pause, "Pause");
            if (item.State == ArchiveQueueState.Paused)
                SetAction(row, PauseColumn, ActionIcon.Play, "Resume");
            if (item.State != ArchiveQueueState.Running)
                SetAction(row, RemoveColumn, ActionIcon.Trash, "Remove from queue");
        }
        jobs.ResumeLayout();
        if (selection is not null)
            jobs.CurrentCell = selection.Cells[column];
        if (top >= 0 && jobs.Rows.Count > 0 && jobs.Visible)
            jobs.FirstDisplayedScrollingRowIndex = Math.Min(top, jobs.Rows.Count - 1);
    }

    private void AddActionColumn() => jobs.Columns.Add(new DataGridViewImageColumn
    {
        Width = 34,
        Resizable = DataGridViewTriState.False,
        DefaultCellStyle = new DataGridViewCellStyle { NullValue = null, Padding = Padding.Empty }
    });

    private static void SetAction(DataGridViewRow row, int column, ActionIcon icon, string label)
    {
        var cell = (DataGridViewImageCell)row.Cells[column];
        cell.Value = ActionIcons.Get(icon, DarkTheme.Ink, 15);
        cell.Description = label;
        cell.ToolTipText = label;
    }

    private bool InvokeAction(int row, int column)
    {
        if (row < 0 || (column != PauseColumn && column != RemoveColumn) ||
            jobs.Rows[row].Tag is not ArchiveQueueItem item || jobs.Rows[row].Cells[column].Value is null)
            return false;
        if (column == RemoveColumn)
            RemoveItem?.Invoke(item);
        else
            TogglePause?.Invoke(item);
        return true;
    }
}
