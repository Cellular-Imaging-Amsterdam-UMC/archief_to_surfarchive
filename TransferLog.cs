using System.Diagnostics;

namespace SurfArchive;

internal enum TransferState
{
    Preparing, CheckingStatus, RequestingTape, TapeQueue, RestoringTape, Transferring, Verifying,
    Ready, Completed, Cancelled, Failed, Skipped
}

internal sealed class TransferJob(string name, string direction, string source, string target, long size,
    bool countItems = false, bool tape = false)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object sync = new();
    private readonly Queue<(double Time, long Bytes)> samples = new();
    private double? copyStart;
    private double? copyEnd;
    private double lastProgress;
    private double lastSample;
    private long copied;
    private long total = Math.Max(size, 0);

    public string Name => name;
    public string Direction { get; set; } = direction;
    public bool CountItems => countItems;
    public bool Tape => tape;
    public List<string> Notes { get; } = [$"Source: {source}", $"Destination: {target}"];
    private TransferState state = TransferState.Preparing;

    public void SetPhase(TransferState phase)
    {
        lock (sync)
            state = phase;
    }

    public void Report(TransferProgress update)
    {
        lock (sync)
        {
            if (copyEnd is not null || update.Bytes < copied || state is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
                return;

            double now = clock.Elapsed.TotalSeconds;
            total = update.Total;
            if (copyStart is null)
            {
                copyStart = now;
                lastProgress = now;
                lastSample = now;
                samples.Enqueue((now, 0));
                state = TransferState.Transferring;
            }
            if (update.Bytes > copied)
            {
                copied = update.Bytes;
                lastProgress = now;
                if (now - lastSample >= 0.25)
                {
                    samples.Enqueue((now, copied));
                    lastSample = now;
                }
                while (samples.Count > 1 && now - samples.Peek().Time > 5)
                    samples.Dequeue();
            }
            if (copied >= total)
            {
                copyEnd = now;
                state = TransferState.Verifying;
            }
        }
    }

    public void Finish(TransferState outcome)
    {
        lock (sync)
        {
            clock.Stop();
            if (copyStart is not null)
                copyEnd ??= clock.Elapsed.TotalSeconds;
            state = outcome;
        }
    }

    public TransferSnapshot Snapshot()
    {
        lock (sync)
        {
            var elapsed = clock.Elapsed;
            double now = elapsed.TotalSeconds;
            double speed = 0;
            if (state == TransferState.Transferring && now - lastProgress <= 5 &&
                samples.TryPeek(out var sample) && now - sample.Time >= 0.5)
                speed = Math.Max(0, (copied - sample.Bytes) / (now - sample.Time));
            TimeSpan? eta = state == TransferState.Transferring && speed > 0 && total > copied
                ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, (total - copied) / speed))
                : null;
            TimeSpan? copyTime = copyStart is { } start ? TimeSpan.FromSeconds((copyEnd ?? now) - start) : null;
            return new(state, state == TransferState.Completed ? total : copied, total, speed, eta, copyTime, elapsed);
        }
    }

    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1e12 => $"{bytes / 1e12:0.#} TB",
        >= 1e9 => $"{bytes / 1e9:0.#} GB",
        >= 1e6 => $"{bytes / 1e6:0.#} MB",
        >= 1e3 => $"{bytes / 1e3:0.#} KB",
        _ => $"{bytes:0} B"
    };

    public static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes}:{time.Seconds:00}";
}

internal record TransferSnapshot(TransferState State, long Copied, long Total, double Speed,
    TimeSpan? Eta, TimeSpan? CopyTime, TimeSpan Elapsed)
{
    public int Percent => State is TransferState.Completed or TransferState.Verifying ? 100
        : Total == 0 ? 0 : (int)Math.Clamp(100.0 * Copied / Total, 0, 100);
    public bool WaitingForTape => State is TransferState.RequestingTape or TransferState.TapeQueue or TransferState.RestoringTape;
}

internal sealed class TransferLog : UserControl
{
    private readonly DataGridView jobs = new BufferedGrid
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, Font = DarkTheme.Small,
        BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        GridColor = DarkTheme.Border, BackgroundColor = DarkTheme.Panel, EnableHeadersVisualStyles = false,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
        ColumnHeadersHeight = 27, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        AccessibleName = "Activity"
    };
    private readonly Label summary = DarkTheme.Label("No activity yet", DarkTheme.Small, DarkTheme.Muted);
    private readonly TextBox details = DarkTheme.Input("Transfer details and archive status");
    private readonly Button cancel = DarkTheme.Button("Cancel transfer");
    private readonly List<string> activity = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly Dictionary<TransferJob, DataGridViewRow> activeRows = [];

    public TransferLog()
    {
        Dock = DockStyle.Fill;
        BackColor = DarkTheme.Panel;
        Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 61));
        Controls.Add(layout);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new(SizeType.AutoSize));
        heading.Controls.Add(DarkTheme.Row(DarkTheme.Label("↔", DarkTheme.Heading, DarkTheme.Mint),
            DarkTheme.Label("  Activity", DarkTheme.Heading), summary), 0, 0);
        summary.Margin = new Padding(14, 4, 0, 0);
        heading.Controls.Add(cancel, 1, 0);
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        cancel.Enabled = false;
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(jobs, 0, 1);
        layout.Controls.Add(details, 0, 2);
        details.Multiline = true;
        details.ReadOnly = true;
        details.WordWrap = false;
        details.ScrollBars = ScrollBars.Both;
        details.Dock = DockStyle.Fill;
        details.Margin = new Padding(0, 5, 0, 0);
        DarkTheme.Explorer(details);

        jobs.RowTemplate.Height = 29;
        jobs.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DarkTheme.Panel, ForeColor = DarkTheme.Ink,
            SelectionBackColor = DarkTheme.Selected, SelectionForeColor = DarkTheme.Ink,
            Padding = new Padding(5, 0, 5, 0)
        };
        jobs.AlternatingRowsDefaultCellStyle.BackColor = DarkTheme.Page;
        jobs.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DarkTheme.Raised, ForeColor = DarkTheme.Muted,
            SelectionBackColor = DarkTheme.Raised, SelectionForeColor = DarkTheme.Muted,
            Padding = new Padding(5, 0, 5, 0)
        };
        AddColumn("File", 210, fill: true);
        AddColumn("Action", 144);
        AddColumn("Status", 121);
        AddColumn("Progress", 131);
        AddColumn("Processed", 135);
        AddColumn("Speed", 83);
        AddColumn("ETA", 68);
        AddColumn("Step time", 88);
        AddColumn("Total time", 75);
        jobs.CellPainting += PaintProgress;
        jobs.SelectionChanged += (_, _) => { ShowDetails(); SelectionChanged?.Invoke(); };
        timer.Tick += (_, _) =>
        {
            foreach (var row in activeRows.Values)
                UpdateRow(row);
        };
        timer.Start();
    }

    public event Action? CancelRequested;
    public event Action? SelectionChanged;
    public TransferJob? SelectedJob => jobs.CurrentRow?.Tag as TransferJob;

    public void Add(TransferJob job)
    {
        var row = new DataGridViewRow { Tag = job };
        row.CreateCells(jobs, job.Name, job.Direction);
        jobs.Rows.Insert(0, row);
        jobs.ClearSelection();
        jobs.CurrentCell = row.Cells[0];
        row.Selected = true;
        row.Cells[0].ToolTipText = job.Name;
        activeRows.Add(job, row);
        UpdateRow(row);
        UpdateSummary();
        ShowDetails(job);
    }

    public void Finish(TransferJob job, TransferState state)
    {
        job.Finish(state);
        if (activeRows.Remove(job, out var row))
            UpdateRow(row);
        UpdateSummary();
        ShowDetails();
    }

    public void SetCancel(string caption, bool enabled)
    {
        cancel.Text = caption;
        cancel.Enabled = enabled;
    }

    public void Log(string message, TransferJob? job = null)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (job is not null)
        {
            job.Notes.Add(line);
            if (job.Tape && job.Notes.Count > 122)
                job.Notes.RemoveAt(2);
        }
        else
            activity.Add(line);
        if (job is null || job == SelectedJob)
            ShowDetails();
    }

    private void ShowDetails(TransferJob? preferred = null)
    {
        var selected = preferred ?? jobs.CurrentRow?.Tag as TransferJob;
        details.Text = string.Join(Environment.NewLine, activity.TakeLast(6)
            .Concat(selected?.Notes ?? []));
        if (!details.Focused)
        {
            details.SelectionStart = details.TextLength;
            details.ScrollToCaret();
        }
    }

    private void UpdateSummary() => summary.Text = jobs.Rows.Count switch
    {
        0 => "No activity yet",
        1 => "1 activity",
        _ => $"{jobs.Rows.Count:N0} activities · newest first"
    };

    private void AddColumn(string title, int width, bool fill = false) =>
        jobs.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = title, Width = width, MinimumWidth = fill ? 130 : 45,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

    private static void UpdateRow(DataGridViewRow row)
    {
        if (row.Tag is not TransferJob job)
            return;
        var snapshot = job.Snapshot();
        bool restoreOnly = job.Tape && snapshot.CopyTime is null;
        row.Cells[1].Value = job.Direction;
        row.Cells[2].Value = snapshot.State switch
        {
            TransferState.CheckingStatus => "Checking status",
            TransferState.RequestingTape => "Requesting tape",
            TransferState.TapeQueue => "Waiting for tape",
            TransferState.RestoringTape => "Restoring tape",
            TransferState.Ready => "Ready to download",
            _ => snapshot.State.ToString()
        };
        row.Cells[2].Style.ForeColor = snapshot.State switch
        {
            TransferState.Completed or TransferState.Ready => DarkTheme.Mint,
            TransferState.Failed => DarkTheme.Error,
            TransferState.Cancelled or TransferState.RequestingTape or TransferState.TapeQueue
                or TransferState.RestoringTape => DarkTheme.Amber,
            _ => DarkTheme.Ink
        };
        row.Cells[3].Value = restoreOnly ? snapshot.WaitingForTape ? "Tape restore" : "—"
            : snapshot.State == TransferState.Skipped ? "—" : snapshot.Percent;
        row.Cells[4].Value = restoreOnly || snapshot.State == TransferState.Skipped
            ? "—" : $"{Format(snapshot.Copied)} / {Format(snapshot.Total)}";
        row.Cells[5].Value = snapshot.Speed > 0 ? Format(snapshot.Speed) + "/s" : "—";
        row.Cells[6].Value = snapshot.Eta is { } eta ? TransferJob.FormatTime(eta + TimeSpan.FromMilliseconds(999)) : "—";
        row.Cells[7].Value = snapshot.CopyTime is { } copy ? TransferJob.FormatTime(copy) : "—";
        row.Cells[8].Value = TransferJob.FormatTime(snapshot.Elapsed);
        string Format(double value) => job.CountItems ? $"{value:N0} items" : TransferJob.FormatBytes(value);
    }

    private void PaintProgress(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 3 || e.Value is not int percent || e.Graphics is null)
            return;
        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
        var bar = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - 6) / 2,
            Math.Max(0, e.CellBounds.Width - 50), 6);
        using var track = new SolidBrush(DarkTheme.Border);
        using var fill = new SolidBrush(DarkTheme.Mint);
        e.Graphics.FillRectangle(track, bar);
        e.Graphics.FillRectangle(fill, new Rectangle(bar.X, bar.Y, bar.Width * percent / 100, bar.Height));
        var label = new Rectangle(bar.Right + 2, e.CellBounds.Y, 36, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, $"{percent}%", DarkTheme.Small, label, DarkTheme.Ink,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid()
        {
            DoubleBuffered = true;
            DarkTheme.Explorer(VerticalScrollBar);
            DarkTheme.Explorer(HorizontalScrollBar);
        }
    }
}
