
namespace SurfArchive;

internal sealed class MainWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(25, 45, 65);
    private static readonly Color Accent = Color.FromArgb(0, 108, 120);
    private static readonly Color Background = Color.FromArgb(241, 245, 249);
    private const string DefaultPath = @"L:\Archief\Active";

    private readonly TextBox username = new() { Width = 115, PlaceholderText = "Gebruikersnaam" };
    private readonly TextBox password = new() { Width = 145, PlaceholderText = "Wachtwoord", UseSystemPasswordChar = true };
    private readonly CheckBox remember = new() { Text = "Lokaal bewaren", AutoSize = true, Margin = new(8, 7, 8, 3) };
    private readonly Button connect = ButtonOf("Verbinden", primary: true);
    private readonly Button forget = ButtonOf("Wissen");
    private readonly Label connection = new() { Text = "Niet verbonden", AutoSize = true, Margin = new(12, 8, 3, 3) };

    private readonly ComboBox drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 65 };
    private readonly TextBox localAddress = new() { Dock = DockStyle.Fill };
    private readonly TextBox remoteAddress = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly ListView localList = CreateList();
    private readonly ListView remoteList = CreateList();
    private readonly Button localUp = ButtonOf("Omhoog");
    private readonly Button localGo = ButtonOf("Ga");
    private readonly Button archiveShortcut = ButtonOf("Archief");
    private readonly Button remoteUp = ButtonOf("Omhoog");
    private readonly Button remoteRefresh = ButtonOf("Vernieuwen");

    private readonly Button upload = ButtonOf("Upload →", primary: true);
    private readonly Button download = ButtonOf("← Download");
    private readonly Button probe = ButtonOf("Proefupload");
    private readonly Button state = ButtonOf("Archiefstatus");
    private readonly Button newFolder = ButtonOf("Nieuwe SURF-map");
    private readonly Button cancel = ButtonOf("Annuleren");
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Label message = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox details = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = true, BackColor = Color.White, Dock = DockStyle.Fill };

    private ArchiveSession? archive;
    private string localPath = "";
    private string remotePath = "";
    private CancellationTokenSource? operation;
    private bool exitAfterOperation;

    public MainWindow()
    {
        Text = "SURF Archief";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Background;
        MinimumSize = new Size(950, 600);
        ClientSize = new Size(1220, 740);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        foreach (int height in new[] { 64, 48, -1, 49, 31, 100 })
            layout.RowStyles.Add(height < 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, height));
        Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Margin = Padding.Empty };
        header.Controls.Add(new Label
        {
            Text = "SURF Archief",
            AutoSize = true, ForeColor = Color.White, Location = new Point(16, 14),
            Font = new Font("Segoe UI Semibold", 21)
        });
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(Flow(username, password, remember, connect, forget, connection), 0, 1);

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(10, 0, 10, 0) };
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.Controls.Add(Pane("Windows", localAddress, localList, drives, archiveShortcut, localUp, localGo), 0, 0);
        panes.Controls.Add(Pane("SURF Data Archive", remoteAddress, remoteList, remoteUp, remoteRefresh), 1, 0);
        layout.Controls.Add(panes, 0, 2);
        layout.Controls.Add(Flow(upload, download, probe, state, newFolder, cancel), 0, 3);

        var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(15, 0, 15, 0) };
        status.ColumnStyles.Add(new(SizeType.Percent, 100));
        status.ColumnStyles.Add(new(SizeType.Absolute, 185));
        status.Controls.Add(message, 0, 0);
        status.Controls.Add(progressBar, 1, 0);
        layout.Controls.Add(status, 0, 4);
        details.Margin = new(15, 3, 15, 12);
        layout.Controls.Add(details, 0, 5);

        connect.Click += async (_, _) => await Perform(ToggleConnection);
        forget.Click += (_, _) =>
        {
            try
            {
                Credentials.Forget();
                username.Clear();
                password.Clear();
                remember.Checked = false;
                SetMessage("Bewaarde inloggegevens gewist.");
            }
            catch (Exception error) { ShowError(error); }
        };
        password.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && connect.Enabled) { e.SuppressKeyPress = true; connect.PerformClick(); }
        };

        foreach (string drive in Directory.GetLogicalDrives())
            drives.Items.Add(drive);
        drives.SelectionChangeCommitted += async (_, _) =>
        {
            if (drives.SelectedItem is string path)
                await Perform(_ => ShowLocal(path));
        };
        localGo.Click += async (_, _) => await Perform(_ => ShowLocal(localAddress.Text));
        localAddress.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && localGo.Enabled) { e.SuppressKeyPress = true; localGo.PerformClick(); }
        };
        archiveShortcut.Click += async (_, _) => await Perform(_ => ShowLocal(DefaultPath));
        localUp.Click += async (_, _) => await Perform(_ => ShowLocal(Directory.GetParent(localPath)!.FullName));
        remoteUp.Click += async (_, _) => await Perform(_ => ShowRemote(remotePath[..remotePath.LastIndexOf('/')]));
        remoteRefresh.Click += async (_, _) => await Perform(_ => ShowRemote(remotePath));

        localList.ItemActivate += async (_, _) =>
        {
            if (Selected(localList) is { IsDirectory: true } entry)
                await Perform(_ => ShowLocal(Path.Combine(localPath, entry.Name)));
        };
        remoteList.ItemActivate += async (_, _) =>
        {
            if (Selected(remoteList) is { IsDirectory: true } entry)
                await Perform(_ => ShowRemote(ArchiveSession.Child(remotePath, entry.Name)));
        };
        localList.ItemSelectionChanged += (_, _) => UpdateControls();
        remoteList.ItemSelectionChanged += (_, _) => UpdateControls();
        upload.Click += async (_, _) => await Perform(token => Transfer(true, token));
        download.Click += async (_, _) => await Perform(token => Transfer(false, token));
        probe.Click += async (_, _) => await Perform(Probe);
        state.Click += async (_, _) => await Perform(async _ =>
        {
            string path = ArchiveSession.Child(remotePath, Selected(remoteList)!.Name);
            Log(await Task.Run(() => archive!.Status(path)));
            SetMessage("Archiefstatus opgehaald.");
        });
        newFolder.Click += async (_, _) => await Perform(CreateFolder);
        cancel.Click += (_, _) => RequestCancel();
        FormClosing += OnClosing;
        FormClosed += (_, _) => archive?.Dispose();
        Shown += async (_, _) => await Perform(async _ =>
        {
            try
            {
                var supplied = Credentials.Load();
                if (supplied is not null)
                {
                    username.Text = supplied.Username;
                    password.Text = supplied.Password;
                    remember.Checked = true;
                }
            }
            catch (Exception error) { Log("Bewaard account kon niet worden geopend. " + error.Message); }
            try { await ShowLocal(DefaultPath); }
            catch (Exception error)
            {
                Log("Archiefschijf: " + error.Message);
                await ShowLocal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            }
        });
        UpdateControls();
    }

    private static Button ButtonOf(string label, bool primary = false) => new()
    {
        Text = label, AutoSize = true, MinimumSize = new Size(0, 31),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
        ForeColor = primary ? Color.White : Ink, Padding = new(5, 0, 5, 0), Margin = new(3, 1, 3, 1)
    };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 4, 0, 0), WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Control Pane(string title, TextBox address, ListView list, params Control[] navigation)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new(9), ColumnCount = 1, RowCount = 4 };
        panel.RowStyles.Add(new(SizeType.Absolute, 27));
        panel.RowStyles.Add(new(SizeType.Absolute, 36));
        panel.RowStyles.Add(new(SizeType.Absolute, 32));
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 13), AutoSize = true }, 0, 0);
        var bar = Flow(navigation);
        bar.Padding = Padding.Empty;
        bar.Margin = Padding.Empty;
        panel.Controls.Add(bar, 0, 1);
        panel.Controls.Add(address, 0, 2);
        panel.Controls.Add(list, 0, 3);
        return panel;
    }

    private static ListView CreateList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        list.Columns.Add("Naam", 240);
        list.Columns.Add("Grootte", 98, HorizontalAlignment.Right);
        list.Columns.Add("Gewijzigd", 137);
        list.SizeChanged += (_, _) =>
        {
            list.Columns[0].Width = Math.Max(120, list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width - 19);
        };
        return list;
    }

    private static ArchiveEntry? Selected(ListView list) =>
        list.SelectedItems.Count == 1 ? (ArchiveEntry?)list.SelectedItems[0].Tag : null;

    private static void Render(ListView list, IEnumerable<ArchiveEntry> entries)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            var rows = entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new ListViewItem(new[]
                {
                    entry.Name, entry.IsDirectory ? "Map" : FormatSize(entry.Size), entry.Modified.ToString("dd-MM-yyyy HH:mm")
                }) { Tag = entry });
            list.Items.AddRange(rows.ToArray());
        }
        finally { list.EndUpdate(); }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => $"{bytes / 1e12:0.##} TB",
        >= 1_000_000_000 => $"{bytes / 1e9:0.##} GB",
        >= 1_000_000 => $"{bytes / 1e6:0.##} MB",
        >= 1_000 => $"{bytes / 1e3:0.##} kB",
        _ => $"{bytes} B"
    };

    private async Task ShowLocal(string path)
    {
        var result = await Task.Run(() =>
        {
            string absolute = Path.GetFullPath(path);
            var entries = new DirectoryInfo(absolute).EnumerateFileSystemInfos().Select(info => new ArchiveEntry(
                info.Name, info is DirectoryInfo, info is FileInfo file ? file.Length : 0, info.LastWriteTime)).ToList();
            return (absolute, entries);
        });
        localPath = result.absolute;
        localAddress.Text = localPath;
        string? root = Path.GetPathRoot(localPath);
        drives.SelectedIndex = root is null ? -1 : drives.Items.IndexOf(root);
        Render(localList, result.entries);
        SetMessage($"{result.entries.Count} Windows-items");
    }

    private async Task ShowRemote(string path)
    {
        var entries = await Task.Run(() => archive!.List(path));
        remotePath = path;
        remoteAddress.Text = path;
        Render(remoteList, entries);
        SetMessage($"{entries.Count} SURF-items");
    }

    private async Task ToggleConnection(CancellationToken _)
    {
        if (archive is not null)
        {
            await Task.Run(archive.Dispose);
            archive = null;
            remotePath = "";
            remoteAddress.Clear();
            remoteList.Items.Clear();
            SetMessage("Verbinding met SURF verbroken.");
            return;
        }

        var supplied = new Credentials(username.Text.Trim(), password.Text);
        if (supplied.Username.Length == 0 || supplied.Password.Length == 0)
            throw new InvalidOperationException("Vul je SURF-gebruikersnaam en wachtwoord in.");
        var candidate = await Task.Run(() => new ArchiveSession(supplied));
        archive = candidate;
        try { await ShowRemote(candidate.Root); }
        catch
        {
            candidate.Dispose();
            archive = null;
            throw;
        }
        if (remember.Checked)
            supplied.Save();
        else
            Credentials.Forget();
        Log("SFTP verbonden met " + ArchiveSession.Host);
    }

    private async Task Transfer(bool sending, CancellationToken token)
    {
        var reporter = TransferReporter();
        if (sending)
        {
            string source = Path.Combine(localPath, Selected(localList)!.Name);
            await Task.Run(() => archive!.Upload(source, remotePath, reporter, token));
            await ShowRemote(remotePath);
        }
        else
        {
            string source = ArchiveSession.Child(remotePath, Selected(remoteList)!.Name);
            await Task.Run(() => archive!.Download(source, localPath, reporter, token));
            await ShowLocal(localPath);
        }
        SetMessage("Kopie voltooid. Grootte gecontroleerd.");
        Log(message.Text);
    }

    private async Task Probe(CancellationToken token)
    {
        string source = Path.Combine(localPath, Selected(localList)!.Name);
        string question = $"Tijdelijk uploaden en opruimen op SURF?\n\n{Path.GetFileName(source)}";
        if (MessageBox.Show(this, question, "Proefupload", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        var reporter = TransferReporter();
        await Task.Run(() => archive!.VerifyUpload(source, reporter,
            line => Invoke(() => Log(line)), token));
        SetMessage("Proefupload geslaagd en verwijderd.");
    }

    private async Task CreateFolder(CancellationToken _)
    {
        using var dialog = new Form { Text = "Nieuwe SURF-map", ClientSize = new Size(370, 86),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var name = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Mapnaam" };
        var accept = ButtonOf("Maken", primary: true);
        accept.DialogResult = DialogResult.OK;
        dialog.Controls.Add(Flow(accept));
        dialog.Controls.Add(name);
        dialog.AcceptButton = accept;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string path = ArchiveSession.Child(remotePath, name.Text.Trim());
        await Task.Run(() => archive!.MakeDirectory(path));
        await ShowRemote(remotePath);
    }

    private IProgress<TransferProgress> TransferReporter()
    {
        long next = 0;
        return new Callback<TransferProgress>(value =>
        {
            long now = Environment.TickCount64;
            if (now < Interlocked.Read(ref next) && value.Bytes != value.Total)
                return;
            Interlocked.Exchange(ref next, now + 125);
            BeginInvoke(() =>
            {
                if (operation is null || operation.IsCancellationRequested)
                    return;
                progressBar.Value = value.Total == 0 ? 1000 : (int)Math.Clamp(1000.0 * value.Bytes / value.Total, 0, 1000);
                SetMessage($"{value.Name}   ·   {FormatSize(value.Bytes)} / {FormatSize(value.Total)}");
            });
        });
    }

    private async Task Perform(Func<CancellationToken, Task> work)
    {
        if (operation is not null)
            return;
        using var source = new CancellationTokenSource();
        operation = source;
        progressBar.Value = 0;
        SetMessage("Bezig…");
        UpdateControls();
        try { await work(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { SetMessage("Geannuleerd."); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            operation = null;
            UpdateControls();
            if (exitAfterOperation)
                BeginInvoke(Close);
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (operation is null)
            return;
        e.Cancel = true;
        exitAfterOperation = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        operation?.Cancel();
        cancel.Enabled = false;
        SetMessage("Wachten tot de bewerking en het opruimen klaar zijn…");
    }

    private void UpdateControls()
    {
        bool idle = operation is null;
        bool online = archive is not null;
        username.Enabled = password.Enabled = remember.Enabled = forget.Enabled = idle && !online;
        connect.Enabled = idle;
        connect.Text = online ? "Verbreken" : "Verbinden";
        connection.Text = online ? "SFTP verbonden" : "Niet verbonden";
        localList.Enabled = drives.Enabled = archiveShortcut.Enabled = localGo.Enabled = localAddress.Enabled = idle;
        localUp.Enabled = idle && localPath.Length > 0 && Directory.GetParent(localPath) is not null;
        remoteList.Enabled = remoteRefresh.Enabled = newFolder.Enabled = idle && online;
        remoteUp.Enabled = idle && online && remotePath.Length > archive!.Root.Length;
        bool localFile = Selected(localList) is { IsDirectory: false };
        bool remoteFile = Selected(remoteList) is { IsDirectory: false };
        probe.Enabled = idle && online && localFile;
        upload.Enabled = probe.Enabled;
        state.Enabled = idle && online && remoteFile;
        download.Enabled = state.Enabled && localPath.Length > 0;
        cancel.Enabled = operation is { IsCancellationRequested: false };
    }

    private void Log(string line)
    {
        details.AppendText($"[{DateTime.Now:HH:mm:ss}] {line.ReplaceLineEndings(Environment.NewLine)}{Environment.NewLine}");
    }

    private void SetMessage(string text) => message.Text = text;

    private void ShowError(Exception error)
    {
        SetMessage(error.Message);
        Log(error.Message);
    }
}
