namespace SurfArchive;

internal sealed class ArchiveQueuePanel : UserControl
{
    public const int EmptyHeight = 52;
    private const int ProgressColumn = 3;
    private const int TimeColumn = 4;
    private const int RunColumn = 5;
    private const int RemoveColumn = 6;
    private readonly ActivityGrid jobs = new() { AccessibleName = "Queue items" };
    private readonly Label summary = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly Button removeFinished = DarkTheme.Button("Remove all finished", ActionIcon.Trash);
    private readonly ToolStripMenuItem browseRecovery = new("Choose recovery folder…");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ContextMenuStrip queueMenu = new()
    {
        BackColor = DarkTheme.Raised, ForeColor = DarkTheme.Ink, Font = DarkTheme.Small, ShowImageMargin = false
    };
    private bool recoveryAvailable;
    private DataGridViewRow? runningRow;
    private TransferJob? directUpload;

    public ArchiveQueuePanel()
    {
        AccessibleName = "Transfer queue";
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
        browseRecovery.Click += (_, _) => BrowseRecovery?.Invoke();
        queueMenu.Items.Add(browseRecovery);
        ContextMenuStrip = queueMenu;
        jobs.ContextMenuStrip = queueMenu;
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(jobs, 0, 1);
        Controls.Add(layout);

        jobs.AddTextColumn("File or archive", 210, fill: true);
        jobs.AddTextColumn("SURF destination", 280, fill: true);
        jobs.AddTextColumn("Status", 121);
        jobs.AddProgressColumn(131);
        jobs.AddTextColumn("Total time", 90);
        jobs.AddActionColumn();
        jobs.AddActionColumn();
        jobs.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                InvokeAction(e.RowIndex, e.ColumnIndex);
        };
        jobs.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 2 &&
                jobs.Rows[e.RowIndex].Tag is ArchiveQueueItem { State: ArchiveQueueState.Failed } item &&
                jobs.Rows[e.RowIndex].Cells[2].ToolTipText is { Length: > 0 } detail)
                MessageBox.Show(this, detail, item.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        jobs.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && jobs.CurrentCell is { } cell &&
                InvokeAction(cell.RowIndex, cell.ColumnIndex))
                e.Handled = e.SuppressKeyPress = true;
        };
        timer.Tick += (_, _) =>
        {
            if (runningRow?.Tag is ArchiveQueueItem item)
            {
                runningRow.Cells[TimeColumn].Value = FormatElapsed(item);
                if (item.DirectUpload && directUpload is { } upload)
                    runningRow.Cells[ProgressColumn].Value = Math.Min(upload.Snapshot().Percent ?? 0, 99);
            }
        };
        ShowItems([]);
    }

    public event Action<ArchiveQueueItem>? TogglePause;
    public event Action<ArchiveQueueItem>? RemoveItem;
    public event Action? RemoveFinished;
    public event Action<ArchiveQueueItem>? ResumeUpload;
    public event Action? BrowseRecovery;
    public event EventHandler? HorizontalScrollbarChanged
    {
        add => jobs.HorizontalScrollbarChanged += value;
        remove => jobs.HorizontalScrollbarChanged -= value;
    }

    public void TrackDirectUpload(TransferJob? job) => directUpload = job;

    public int HeightFor(int rows)
    {
        int header = (int)Math.Round(EmptyHeight * DeviceDpi / 96f);
        return rows == 0 ? header : header + jobs.HeightFor(rows) + (int)Math.Round(6 * DeviceDpi / 96f);
    }

    public void SetRecoveryAvailable(bool available)
    {
        browseRecovery.Enabled = available;
        if (recoveryAvailable == available)
            return;
        recoveryAvailable = available;
        foreach (DataGridViewRow row in jobs.Rows)
            if (row.Tag is ArchiveQueueItem { CanResumeUpload: true })
                SetRecoveryAction(row);
    }

    public void ShowItems(IReadOnlyList<ArchiveQueueItem> items)
    {
        var counts = items.GroupBy(item => item.State).ToDictionary(group => group.Key, group => group.Count());
        summary.Text = items.Count == 0 ? "No jobs queued" : string.Join("  ·  ", counts.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Value:N0} {(pair.Key == ArchiveQueueState.Queued ? "waiting" : pair.Key.ToString().ToLowerInvariant())}"));
        removeFinished.Enabled = counts.ContainsKey(ArchiveQueueState.Finished);
        jobs.Visible = items.Count > 0;

        string? selectedId = (jobs.CurrentRow?.Tag as ArchiveQueueItem)?.LocalName;
        int column = jobs.CurrentCell?.ColumnIndex ?? 0;
        int top = jobs.FirstDisplayedScrollingRowIndex;
        DataGridViewRow? selection = null;
        runningRow = null;
        jobs.SuspendLayout();
        jobs.Rows.Clear();
        foreach (var item in items.Reverse().OrderByDescending(item => item.State == ArchiveQueueState.Running))
        {
            var progress = item.Progress;
            int totalSteps = progress?.Total ?? item.Steps;
            int steps = item.State == ArchiveQueueState.Finished ? totalSteps : progress?.Completed ?? 0;
            int percent = (int)(100L * steps / totalSteps);
            var row = jobs.Rows[jobs.Rows.Add(item.Name, item.RemotePath, item.State.ToString(), percent, FormatElapsed(item))];
            row.Tag = item;
            if (item.State == ArchiveQueueState.Running)
                runningRow = row;
            if (item.LocalName == selectedId)
                selection = row;
            row.Cells[0].ToolTipText = item.DirectUpload
                ? $"Direct upload\n{Path.Combine(item.Source, item.Name)}" : item.Source;
            row.Cells[1].ToolTipText = item.RemotePath;
            row.Cells[ProgressColumn].ToolTipText = item.DirectUpload ? "Direct upload progress" : $"{steps} of {totalSteps} steps complete";
            row.Cells[TimeColumn].ToolTipText = "Total running time, including waits during the job";
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
                SetAction(row, RunColumn, ActionIcon.Pause, "Pause");
            else if (item.State == ArchiveQueueState.Paused)
                SetAction(row, RunColumn, ActionIcon.Play, "Resume");
            else if (item.CanResumeUpload)
                SetRecoveryAction(row);
            if (item.State != ArchiveQueueState.Running)
                SetAction(row, RemoveColumn, ActionIcon.Trash, "Remove from queue");
        }
        jobs.ResumeLayout();
        if (selection is not null)
            jobs.CurrentCell = selection.Cells[column];
        if (top >= 0 && jobs.Rows.Count > 0 && jobs.Visible)
            jobs.FirstDisplayedScrollingRowIndex = Math.Min(top, jobs.Rows.Count - 1);
        timer.Enabled = runningRow is not null;
    }

    private static string FormatElapsed(ArchiveQueueItem item) =>
        item.Progress is { } progress ? TransferJob.FormatTime(progress.CurrentElapsed) : "—";

    private void SetRecoveryAction(DataGridViewRow row) =>
        SetAction(row, RunColumn, ActionIcon.Recovery,
            recoveryAvailable ? "Resume archive upload" : "Resume archive upload when the app is idle", recoveryAvailable);

    private static void SetAction(DataGridViewRow row, int column, ActionIcon icon, string label, bool enabled = true)
    {
        var cell = (DataGridViewImageCell)row.Cells[column];
        cell.Value = ActionIcons.Get(icon, enabled ? DarkTheme.Ink : DarkTheme.Muted, 15);
        cell.Description = label;
        cell.ToolTipText = label;
    }

    private bool InvokeAction(int row, int column)
    {
        if (row < 0 || (column != RunColumn && column != RemoveColumn) ||
            jobs.Rows[row].Tag is not ArchiveQueueItem item || jobs.Rows[row].Cells[column].Value is null)
            return false;
        if (column == RemoveColumn)
            RemoveItem?.Invoke(item);
        else if (item.CanResumeUpload)
        {
            if (recoveryAvailable)
                ResumeUpload?.Invoke(item);
        }
        else
            TogglePause?.Invoke(item);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            queueMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
