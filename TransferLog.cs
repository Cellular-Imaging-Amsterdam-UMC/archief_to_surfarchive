using System.Diagnostics;

namespace SurfArchive;

internal enum TransferState
{
    Preparing, Transferring, Verifying, Completed, Cancelled, Failed
}

internal sealed class TransferJob(string name, string direction, string source, string target, long size)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object sync = new();
    private readonly Queue<(double Time, long Bytes)> samples = new();
    private double? firstProgress;
    private double lastProgress;
    private long copied;
    private long total = Math.Max(size, 0);

    public string Name => name;
    public string Direction => direction;
    public List<string> Notes { get; } = [$"Source: {source}", $"Destination: {target}"];
    public TransferState State { get; set; } = TransferState.Preparing;
    public bool Active => State is TransferState.Preparing or TransferState.Transferring or TransferState.Verifying;

    public void Report(TransferProgress update)
    {
        lock (sync)
        {
            if (update.Bytes < copied)
                return;

            total = update.Total;
            if (update.Bytes == copied)
                return;

            double now = clock.Elapsed.TotalSeconds;
            if (firstProgress is null)
            {
                firstProgress = now;
                samples.Enqueue((now, 0));
            }
            copied = update.Bytes;
            lastProgress = now;
            if (samples.Count == 0 || now - samples.Last().Time >= 0.25)
                samples.Enqueue((now, copied));
            while (samples.Count > 1 && now - samples.Peek().Time > 5)
                samples.Dequeue();
        }
    }

    public void Finish(TransferState state)
    {
        State = state;
        clock.Stop();
    }

    public TransferSnapshot Snapshot()
    {
        lock (sync)
        {
            var elapsed = clock.Elapsed;
            double now = elapsed.TotalSeconds;
            double speed = 0;
            if (firstProgress is { } start)
            {
                if (State == TransferState.Completed && lastProgress - start >= 0.25)
                    speed = copied / (lastProgress - start);
                else if (Active && now - lastProgress <= 5 && samples.TryPeek(out var sample) && now - sample.Time >= 0.5)
                    speed = Math.Max(0, (copied - sample.Bytes) / (now - sample.Time));
            }

            var state = State;
            if (Active && copied > 0)
                state = copied >= total ? TransferState.Verifying : TransferState.Transferring;
            if (state == TransferState.Completed)
                return new(state, total, total, speed, null, elapsed);

            TimeSpan? eta = state == TransferState.Transferring && speed > 0 && total > copied
                ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, (total - copied) / speed))
                : null;
            return new(state, copied, total, speed, eta, elapsed);
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
    TimeSpan? Eta, TimeSpan Elapsed)
{
    public int Percent => State == TransferState.Completed ? 100
        : Total == 0 ? 0 : (int)Math.Clamp(100.0 * Copied / Total, 0, 100);
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
        AccessibleName = "Transfers"
    };
    private readonly Label summary = DarkTheme.Label("No transfers yet", DarkTheme.Small, DarkTheme.Muted);
    private readonly TextBox details = DarkTheme.Input("Transfer details and archive status");
    private readonly Button cancel = DarkTheme.Button("Cancel transfer");
    private readonly List<string> activity = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private DataGridViewRow? activeRow;

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
            DarkTheme.Label("  Transfers", DarkTheme.Heading), summary), 0, 0);
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
        AddColumn("Direction", 92);
        AddColumn("Status", 105);
        AddColumn("Progress", 150);
        AddColumn("Transferred", 152);
        AddColumn("Speed", 94);
        AddColumn("ETA", 79);
        AddColumn("Elapsed", 83);
        jobs.CellPainting += PaintProgress;
        jobs.SelectionChanged += (_, _) => ShowDetails();
        timer.Tick += (_, _) =>
        {
            if (activeRow is not null)
                UpdateRow(activeRow);
        };
        timer.Start();
    }

    public event Action? CancelRequested;

    public void Add(TransferJob job)
    {
        var row = new DataGridViewRow { Tag = job };
        row.CreateCells(jobs, job.Name, job.Direction);
        jobs.Rows.Insert(0, row);
        jobs.ClearSelection();
        jobs.CurrentCell = row.Cells[0];
        row.Selected = true;
        row.Cells[0].ToolTipText = job.Name;
        activeRow = row;
        UpdateRow(row);
        UpdateSummary();
        ShowDetails(job);
    }

    public void Finish(TransferJob job, TransferState state)
    {
        job.Finish(state);
        if (activeRow is not null)
            UpdateRow(activeRow);
        activeRow = null;
        UpdateSummary();
        ShowDetails();
    }

    public void SetCancelEnabled(bool enabled) => cancel.Enabled = enabled;

    public void Log(string message, TransferJob? job = null)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (job is not null)
            job.Notes.Add(line);
        else
            activity.Add(line);
        ShowDetails(job);
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
        0 => "No transfers yet",
        1 => "1 transfer",
        _ => $"{jobs.Rows.Count:N0} transfers · newest first"
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
        row.Cells[2].Value = snapshot.State.ToString();
        row.Cells[2].Style.ForeColor = snapshot.State switch
        {
            TransferState.Completed => DarkTheme.Mint,
            TransferState.Failed => DarkTheme.Error,
            TransferState.Cancelled => DarkTheme.Amber,
            _ => DarkTheme.Ink
        };
        row.Cells[3].Value = snapshot.Percent;
        row.Cells[4].Value = $"{TransferJob.FormatBytes(snapshot.Copied)} / {TransferJob.FormatBytes(snapshot.Total)}";
        row.Cells[5].Value = snapshot.Speed > 0 ? TransferJob.FormatBytes(snapshot.Speed) + "/s" : "—";
        row.Cells[6].Value = snapshot.Eta is { } eta ? TransferJob.FormatTime(eta + TimeSpan.FromMilliseconds(999)) : "—";
        row.Cells[7].Value = TransferJob.FormatTime(snapshot.Elapsed);
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
        public BufferedGrid() => DoubleBuffered = true;
    }
}
