namespace SurfArchive;

internal sealed class LogWindow : Form
{
    private readonly SessionLog? session;
    private readonly ListBox files = new()
    {
        AccessibleName = "Log files", Dock = DockStyle.Fill, BackColor = DarkTheme.Page, ForeColor = DarkTheme.Ink,
        Font = DarkTheme.Small, IntegralHeight = false, HorizontalScrollbar = true
    };
    private readonly TextBox content = DarkTheme.Input("Log contents");
    private readonly Label location = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private bool refreshing;
    private LogFile? displayed;

    public LogWindow(SessionLog? session)
    {
        this.session = session;
        DarkTheme.Window(this);
        Text = "Logs";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 610);
        MinimumSize = new Size(680, 390);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2
        };
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        location.AutoSize = false;
        location.AutoEllipsis = true;
        location.UseMnemonic = false;
        location.Dock = DockStyle.Fill;
        location.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(location, 0, 0);

        var panes = new SplitContainer
        {
            Dock = DockStyle.Fill, BackColor = DarkTheme.Border, SplitterWidth = 6,
            Width = ClientSize.Width - layout.Padding.Horizontal, SplitterDistance = 305
        };
        files.DisplayMember = nameof(LogFile.Name);
        content.Multiline = true;
        content.MaxLength = int.MaxValue;
        content.ReadOnly = true;
        content.WordWrap = false;
        content.ScrollBars = ScrollBars.Both;
        content.Dock = DockStyle.Fill;
        DarkTheme.Explorer(files);
        DarkTheme.Explorer(content);
        panes.Panel1.Controls.Add(files);
        panes.Panel2.Controls.Add(content);
        layout.Controls.Add(panes, 0, 1);
        Controls.Add(layout);

        files.SelectedIndexChanged += async (_, _) =>
        {
            if (!refreshing)
                await ShowSelected();
        };
        Shown += async (_, _) =>
        {
            await RefreshFiles();
            if (!IsDisposed)
                refresh.Start();
        };
        refresh.Tick += async (_, _) => await RefreshFiles();
    }

    private async Task RefreshFiles()
    {
        if (refreshing)
            return;
        refreshing = true;
        try
        {
            string folder = session?.Folder ?? SessionLog.DefaultFolder;
            LogFile[] logs = await Task.Run(() => Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*.log")
                    .Select(file => new LogFile(file.FullName, file.Name, file.LastWriteTimeUtc, file.Length))
                    .OrderByDescending(file => file.Modified).ThenByDescending(file => file.Name, StringComparer.Ordinal)
                    .ToArray()
                : []);
            if (IsDisposed)
                return;
            location.Text = session?.Error is { } error ? $"{folder}  ·  Logging error  ·  {error}" : folder;
            string? selection = (files.SelectedItem as LogFile)?.Path;
            if (!logs.SequenceEqual(files.Items.Cast<LogFile>()))
            {
                files.BeginUpdate();
                files.Items.Clear();
                files.Items.AddRange(logs);
                int previous = Array.FindIndex(logs, file => file.Path == selection);
                files.SelectedIndex = previous >= 0 ? previous : logs.Length == 0 ? -1 : 0;
                files.EndUpdate();
            }
            await ShowSelected();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed)
                content.Text = $"Could not list the logs. {error.Message}";
        }
        finally { refreshing = false; }
    }

    private async Task ShowSelected()
    {
        if (files.SelectedItem is not LogFile file)
        {
            displayed = null;
            content.Text = "No log files found.";
            return;
        }
        if (file == displayed)
            return;
        try
        {
            await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(input);
            string log = await reader.ReadToEndAsync();
            if (IsDisposed || !Equals(files.SelectedItem, file))
                return;
            bool following = displayed?.Path != file.Path || content.SelectionStart >= content.TextLength;
            int position = following ? log.Length : content.SelectionStart;
            content.Text = log;
            content.Select(Math.Min(position, content.TextLength), 0);
            content.ScrollToCaret();
            displayed = file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed && Equals(files.SelectedItem, file))
                content.Text = $"Could not read {file.Name}. {error.Message}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            refresh.Dispose();
        base.Dispose(disposing);
    }

    private sealed record LogFile(string Path, string Name, DateTime Modified, long Length);
}
