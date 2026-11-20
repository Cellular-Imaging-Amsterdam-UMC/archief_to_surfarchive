using System.Diagnostics;

namespace SurfArchive;

internal enum TransferState
{
    Preparing, CheckingStatus, RequestingTape, TapeQueue, RestoringTape, Transferring, Verifying,
    Ready, Completed, Cancelled, Failed, Skipped, WaitingForParts, Retrying, UploadStopped
}

internal sealed class TransferJob(string name, string direction, string source, string target, long size,
    bool countItems = false, bool tape = false, bool totalKnown = true, string? working = null, bool showRate = true)
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
    private bool known = totalKnown;

    public string Name => name;
    public string Direction { get; set; } = direction;
    public bool CountItems => countItems;
    public bool Tape => tape;
    public string? Working => working;
    public List<string> Notes { get; } = [$"Source: {source}", $"Destination: {target}"];
    private TransferState state = TransferState.Preparing;

    public void SetPhase(TransferState phase)
    {
        lock (sync)
            state = phase;
    }

    public void SetTotal(long size)
    {
        lock (sync)
        {
            total = size;
            known = true;
        }
    }

    public void Report(TransferProgress update) => Report(update, null);

    public void Report(TransferProgress update, TransferState? phase)
    {
        lock (sync)
        {
            bool restart = phase is TransferState.Retrying or TransferState.UploadStopped;
            if ((!restart && (copyEnd is not null || update.Bytes < copied))
                || state is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
                return;

            double now = clock.Elapsed.TotalSeconds;
            if (update.TotalKnown || !known)
            {
                total = update.Total;
                known = update.TotalKnown;
            }
            if (restart)
            {
                copied = update.Bytes;
                copyEnd = null;
            }
            bool starting = copyStart is null;
            copyStart ??= now;
            if (starting || restart || state is TransferState.WaitingForParts or TransferState.Retrying)
            {
                lastProgress = now;
                lastSample = now;
                samples.Clear();
                samples.Enqueue((now, copied));
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
            if (phase is { } explicitPhase)
                state = explicitPhase;
            else if (known && copied >= total)
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
            if (showRate && state == TransferState.Transferring && now - lastProgress <= 5 &&
                samples.TryPeek(out var sample) && now - sample.Time >= 0.5)
                speed = Math.Max(0, (copied - sample.Bytes) / (now - sample.Time));
            TimeSpan? eta = known && state == TransferState.Transferring && speed > 0 && total > copied
                ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, (total - copied) / speed))
                : null;
            TimeSpan? copyTime = copyStart is { } start ? TimeSpan.FromSeconds((copyEnd ?? now) - start) : null;
            return new(state, state == TransferState.Completed ? total : copied, total, speed, eta, copyTime, elapsed, known);
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
    TimeSpan? Eta, TimeSpan? CopyTime, TimeSpan Elapsed, bool TotalKnown)
{
    public int? Percent => !TotalKnown ? null : State is TransferState.Completed or TransferState.Verifying ? 100
        : Total == 0 ? 0 : (int)Math.Clamp(100.0 * Copied / Total, 0, 100);
    public bool WaitingForTape => State is TransferState.RequestingTape or TransferState.TapeQueue or TransferState.RestoringTape;
}

internal sealed class TransferLog : UserControl
{
    private const int HeadingHeight = 32;
    private const int VisibleRows = 4;
    public const int DisplayHeight = 16 + HeadingHeight + 6 +
        ActivityGrid.HeaderHeight + VisibleRows * ActivityGrid.ItemHeight;
    private readonly ActivityGrid jobs = new() { AccessibleName = "Activity" };
    private readonly Label summary = DarkTheme.Label("No activity yet", DarkTheme.Small, DarkTheme.Muted);
    private readonly Button cancel = DarkTheme.Button("Cancel activity", ActionIcon.Cancel);
    private readonly List<string> activity = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly Dictionary<TransferJob, DataGridViewRow> activeRows = [];
    private readonly SessionLog? session;

    public TransferLog(SessionLog? session = null)
    {
        this.session = session;
        Dock = DockStyle.Fill;
        BackColor = DarkTheme.Panel;
        Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, HeadingHeight));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
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

        jobs.AddTextColumn("File or archive", 210, fill: true);
        jobs.AddTextColumn("Action", 161);
        jobs.AddTextColumn("Status", 121);
        jobs.AddProgressColumn(131);
        jobs.AddTextColumn("Processed", 135);
        jobs.AddTextColumn("Speed", 83);
        jobs.AddTextColumn("ETA", 68);
        jobs.AddTextColumn("Copy time", 88);
        jobs.AddTextColumn("Elapsed", 75);
        jobs.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        jobs.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 2 &&
                jobs.Rows[e.RowIndex].Tag is TransferJob job && job.Snapshot().State == TransferState.Failed &&
                jobs.Rows[e.RowIndex].Cells[2].ToolTipText is { Length: > 0 } detail)
                MessageBox.Show(this, detail, job.Direction, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        timer.Tick += (_, _) =>
        {
            foreach (var row in activeRows.Values)
                UpdateRow(row);
        };
        timer.Start();
    }

    public event Action? CancelRequested;
    public event Action? SelectionChanged;
    public event EventHandler? HorizontalScrollbarChanged
    {
        add => jobs.HorizontalScrollbarChanged += value;
        remove => jobs.HorizontalScrollbarChanged -= value;
    }
    public int HorizontalScrollbarHeight => jobs.HorizontalScrollbarHeight;
    public TransferJob? SelectedJob => jobs.CurrentRow?.Tag as TransferJob;

    public void Add(TransferJob job)
    {
        session?.Write($"{job.Direction} · {job.Name} · Started\n{string.Join(Environment.NewLine, job.Notes)}");
        var row = new DataGridViewRow { Tag = job, Height = jobs.RowTemplate.Height };
        row.CreateCells(jobs, job.Name, job.Direction);
        jobs.Rows.Insert(0, row);
        jobs.ClearSelection();
        jobs.CurrentCell = row.Cells[0];
        row.Selected = true;
        row.Cells[0].ToolTipText = job.Name;
        activeRows.Add(job, row);
        UpdateRow(row);
        UpdateSummary();
    }

    public void Finish(TransferJob job, TransferState state, string? detail = null)
    {
        job.Finish(state);
        string elapsed = TransferJob.FormatTime(job.Snapshot().Elapsed);
        session?.Write($"{job.Direction} · {job.Name} · {state} · {elapsed}" + (detail is null ? "" : $"\n{detail}"));
        if (activeRows.Remove(job, out var row))
        {
            if (detail is not null)
                row.Cells[2].ToolTipText = detail;
            UpdateRow(row);
        }
        UpdateSummary();
    }

    public void SetCancel(string caption, bool enabled)
    {
        cancel.Text = caption;
        cancel.Enabled = enabled;
    }

    public void Log(string message, TransferJob? job = null)
    {
        session?.Write(job is null ? message : $"{job.Direction} · {job.Name} · {message}");
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (job is not null)
        {
            job.Notes.Add(line);
            if (job.Tape && job.Notes.Count > 122)
                job.Notes.RemoveAt(2);
        }
        else
            activity.Add(line);
    }

    private void UpdateSummary() => summary.Text = jobs.Rows.Count switch
    {
        0 => "No activity yet",
        1 => "1 activity",
        _ => $"{jobs.Rows.Count:N0} activities · newest first"
    };

    private static void UpdateRow(DataGridViewRow row)
    {
        if (row.Tag is not TransferJob job)
            return;
        var snapshot = job.Snapshot();
        bool restoreOnly = job.Tape && (snapshot.CopyTime is null || snapshot.WaitingForTape
            || snapshot.State == TransferState.CheckingStatus);
        row.Cells[1].Value = job.Direction;
        row.Cells[2].Value = snapshot.State switch
        {
            TransferState.Transferring when job.Working is { } working => working,
            TransferState.Verifying when job.Working is not null => "Finishing",
            TransferState.CheckingStatus => "Checking status",
            TransferState.RequestingTape => "Requesting tape",
            TransferState.TapeQueue => "Waiting for tape",
            TransferState.RestoringTape => "Restoring tape",
            TransferState.WaitingForParts => "Waiting for parts",
            TransferState.Retrying => "Retry scheduled",
            TransferState.UploadStopped => "Upload stopped",
            TransferState.Ready => "Ready to download",
            _ => snapshot.State.ToString()
        };
        row.Cells[2].Style.ForeColor = snapshot.State switch
        {
            TransferState.Completed or TransferState.Ready => DarkTheme.Mint,
            TransferState.Failed or TransferState.UploadStopped => DarkTheme.Error,
            TransferState.Cancelled or TransferState.RequestingTape or TransferState.TapeQueue
                or TransferState.RestoringTape or TransferState.Retrying => DarkTheme.Amber,
            _ => DarkTheme.Ink
        };
        row.Cells[3].Value = restoreOnly ? snapshot.WaitingForTape ? "Tape restore" : "—"
            : snapshot.State == TransferState.Skipped ? "—" : !snapshot.TotalKnown ? "Total pending" : snapshot.Percent;
        row.Cells[4].Value = restoreOnly || snapshot.State == TransferState.Skipped
            ? "—" : !snapshot.TotalKnown ? Format(snapshot.Copied)
            : job.CountItems ? $"{snapshot.Copied:N0} / {snapshot.Total:N0} items"
            : $"{Format(snapshot.Copied)} / {Format(snapshot.Total)}";
        row.Cells[5].Value = snapshot.Speed > 0 ? Format(snapshot.Speed) + "/s" : "—";
        row.Cells[6].Value = snapshot.Eta is { } eta ? TransferJob.FormatTime(eta + TimeSpan.FromMilliseconds(999)) : "—";
        row.Cells[7].Value = job.Working is null && snapshot.CopyTime is { } copy ? TransferJob.FormatTime(copy) : "—";
        row.Cells[8].Value = TransferJob.FormatTime(snapshot.Elapsed);
        string Format(double value) => job.CountItems ? $"{value:N0} items" : TransferJob.FormatBytes(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            timer.Dispose();
        base.Dispose(disposing);
    }
}
