namespace SurfArchive;

internal sealed class MainWindow : Form
{
    private readonly BrowserPane windows = new("Archief / Windows", local: true);
    private readonly BrowserPane surf = new("SURF Data Archive", local: false);
    private readonly TransferLog transfers = new();
    private readonly Button signIn = DarkTheme.Button("Sign out");
    private readonly Button upload = DarkTheme.Button("Upload to SURF  →", primary: true);
    private readonly Button download = DarkTheme.Button("←  Download to Windows", primary: true);
    private readonly Button probe = DarkTheme.Button("Test upload");
    private readonly Button status = DarkTheme.Button("Archive status");
    private readonly Button newFolder = DarkTheme.Button("+ New folder");
    private readonly Label connection = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Mint);
    private readonly Label feedback = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private ArchiveSession? archive;
    private CancellationTokenSource? operation;
    private bool transferring;
    private bool closeWhenIdle;

    public MainWindow(ArchiveSession? session)
    {
        archive = session;
        DarkTheme.Window(this);
        Text = "SURF Archive";
        MinimumSize = new Size(1040, 660);
        ClientSize = new Size(1410, 810);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();

        windows.AddLocation("Archief Active (L:)", LocalFiles.ActiveArchive);
        foreach (string drive in Directory.GetLogicalDrives())
            windows.AddLocation(drive, drive);
        windows.Navigate += async (path, root) => await Run(_ => NavigateLocal(path, root));
        surf.Navigate += async (path, root) => await Run(_ => NavigateRemote(path, root));
        windows.SelectionChanged += UpdateControls;
        surf.SelectionChanged += UpdateControls;
        signIn.Click += async (_, _) => await Run(ToggleLogin);
        upload.Click += async (_, _) => await Run(token => Copy(toSurf: true, token), transfer: true);
        download.Click += async (_, _) => await Run(token => Copy(toSurf: false, token), transfer: true);
        probe.Click += async (_, _) => await RunProbe();
        status.Click += async (_, _) => await Run(ShowArchiveStatus);
        newFolder.Click += async (_, _) => await Run(MakeFolder);
        transfers.CancelRequested += CancelTransfer;
        Shown += async (_, _) => await Run(InitialLoad);
        FormClosing += OnClosing;
        FormClosed += (_, _) => archive?.Dispose();
        UpdateControls();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (int height in new[] { 58, -1, 43, 230, 25 })
            layout.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = DarkTheme.Panel,
            Padding = new Padding(15, 10, 12, 7), Margin = new Padding(0, 0, 0, 3)
        };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(DarkTheme.Row(DarkTheme.Label("▣", DarkTheme.Brand, DarkTheme.Mint),
            DarkTheme.Label("  SURF Archive", DarkTheme.Brand)), 0, 0);
        connection.Margin = new Padding(0, 7, 17, 0);
        header.Controls.Add(connection, 1, 0);
        header.Controls.Add(signIn, 2, 0);
        layout.Controls.Add(header, 0, 0);

        var panes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(10, 4, 10, 1)
        };
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.Controls.Add(windows, 0, 0);
        panes.Controls.Add(surf, 1, 0);
        layout.Controls.Add(panes, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(13, 5, 13, 1) };
        actions.ColumnStyles.Add(new(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new(SizeType.Percent, 50));
        actions.Controls.Add(DarkTheme.Row(upload, probe), 0, 0);
        actions.Controls.Add(DarkTheme.Row(download, status, newFolder), 1, 0);
        layout.Controls.Add(actions, 0, 2);

        transfers.Margin = new Padding(13, 0, 13, 0);
        layout.Controls.Add(transfers, 0, 3);
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        feedback.Dock = DockStyle.Fill;
        feedback.Margin = new Padding(14, 4, 14, 0);
        layout.Controls.Add(feedback, 0, 4);
    }

    private async Task InitialLoad(CancellationToken _)
    {
        var local = LoadDefaultLocal();
        if (archive is null)
        {
            await local;
            return;
        }
        await Task.WhenAll(local, NavigateRemote(archive.Root, "My archive"));
    }

    private async Task LoadDefaultLocal()
    {
        try { await NavigateLocal(LocalFiles.ActiveArchive, "Archief Active"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            transfers.Log($"Could not open {LocalFiles.ActiveArchive}. {error.Message}");
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            await NavigateLocal(path, "Documents");
        }
    }

    private async Task NavigateLocal(string path, string? root)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var entries = await Task.Run(() => LocalFiles.List(full));
            windows.ShowFolder(full, root, entries);
        }
        catch
        {
            windows.RestoreLocation();
            throw;
        }
    }

    private async Task NavigateRemote(string path, string? root)
    {
        try
        {
            var entries = await Task.Run(() => archive!.List(path));
            surf.ShowFolder(path, root, entries);
        }
        catch
        {
            surf.RestoreLocation();
            throw;
        }
    }

    private async Task ToggleLogin(CancellationToken _)
    {
        if (archive is not null)
        {
            var currentArchive = archive;
            archive = null;
            surf.ClearFolder();
            await Task.Run(currentArchive.Dispose);
            SetFeedback("Signed out of SURF Data Archive.");
            return;
        }

        using var login = new LoginDialog { StartPosition = FormStartPosition.CenterParent };
        if (login.ShowDialog(this) != DialogResult.OK)
            return;

        archive = login.TakeSession();
        await NavigateRemote(archive.Root, "My archive");
        SetFeedback("");
    }

    private async Task Copy(bool toSurf, CancellationToken token)
    {
        BrowserPane origin = toSurf ? windows : surf;
        BrowserPane target = toSurf ? surf : windows;
        int failures = 0;
        foreach (var entry in origin.SelectedFiles)
        {
            token.ThrowIfCancellationRequested();
            string source = toSurf ? Path.Combine(origin.PathShown, entry.Name) : ArchiveSession.Child(origin.PathShown, entry.Name);
            var job = new TransferJob(entry.Name, toSurf ? "Upload →" : "← Download", source, target.PathShown, entry.Size);
            transfers.Add(job);

            try
            {
                var progress = new Callback<TransferProgress>(job.Report);
                if (toSurf)
                    await Task.Run(() => archive!.Upload(source, target.PathShown, progress, token));
                else
                    await Task.Run(() => archive!.Download(source, target.PathShown, progress, token));
                transfers.Log("Copy complete. File size verified.", job);
                transfers.Finish(job, TransferState.Completed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                transfers.Log("Transfer cancelled.", job);
                transfers.Finish(job, TransferState.Cancelled);
                throw;
            }
            catch (Exception error)
            {
                failures++;
                transfers.Log(error.Message, job);
                transfers.Finish(job, TransferState.Failed);
            }
        }

        if (toSurf)
            await NavigateRemote(target.PathShown, null);
        else
            await NavigateLocal(target.PathShown, null);
        SetFeedback(failures == 0 ? "Transfer complete." : $"{failures:N0} transfer(s) failed. See the transfer log.", failures > 0);
    }

    private async Task RunProbe()
    {
        var entry = windows.SelectedFiles.Single();
        string source = Path.Combine(windows.PathShown, entry.Name);
        if (MessageBox.Show(this, $"Upload temporarily, verify and remove the test copy from SURF?\n\n{entry.Name}",
            "Test upload", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        await Run(async token =>
        {
            var job = new TransferJob(entry.Name, "Test upload", source, archive!.Root, entry.Size);
            transfers.Add(job);
            try
            {
                await Task.Run(() => archive.VerifyUpload(source, new Callback<TransferProgress>(job.Report),
                    line => Invoke(() => transfers.Log(line, job)), token));
                transfers.Finish(job, TransferState.Completed);
                SetFeedback("Test upload verified and removed.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                transfers.Log("Test upload cancelled.", job);
                transfers.Finish(job, TransferState.Cancelled);
                throw;
            }
            catch (Exception error)
            {
                transfers.Log(error.Message, job);
                transfers.Finish(job, TransferState.Failed);
                throw;
            }
        }, transfer: true);
    }

    private async Task ShowArchiveStatus(CancellationToken _)
    {
        foreach (var entry in surf.SelectedFiles)
        {
            string path = ArchiveSession.Child(surf.PathShown, entry.Name);
            transfers.Log(await Task.Run(() => archive!.Status(path)));
        }
        SetFeedback("Archive status is shown in the transfer log.");
    }

    private async Task MakeFolder(CancellationToken _)
    {
        using var dialog = new Form
        {
            Text = "New SURF folder", ClientSize = new Size(382, 112), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        DarkTheme.Window(dialog);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(15) };
        layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var name = DarkTheme.Input("Folder name");
        name.PlaceholderText = "Folder name";
        name.Dock = DockStyle.Top;
        var create = DarkTheme.Button("Create", primary: true);
        var cancel = DarkTheme.Button("Cancel");
        create.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        var actions = DarkTheme.Row(cancel, create);
        actions.FlowDirection = FlowDirection.RightToLeft;
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string path = ArchiveSession.Child(surf.PathShown, name.Text.Trim());
        await Task.Run(() => archive!.MakeDirectory(path));
        await NavigateRemote(surf.PathShown, null);
        SetFeedback("Folder created.");
    }

    private async Task Run(Func<CancellationToken, Task> work, bool transfer = false)
    {
        if (operation is not null)
            return;
        using var token = new CancellationTokenSource();
        operation = token;
        transferring = transfer;
        UpdateControls();
        SetFeedback(transfer ? "" : "Loading…");
        try
        {
            await work(token.Token);
            if (feedback.Text == "Loading…")
                SetFeedback("");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetFeedback("Transfer cancelled.");
        }
        catch (Exception error)
        {
            transfers.Log(error.Message);
            SetFeedback(error.Message, error: true);
        }
        finally
        {
            operation = null;
            transferring = false;
            UpdateControls();
            if (closeWhenIdle)
                BeginInvoke(Close);
        }
    }

    private void CancelTransfer()
    {
        if (operation is null || !transferring)
            return;
        operation.Cancel();
        UpdateControls();
        SetFeedback("Cancelling transfer and cleaning up temporary files…");
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (operation is null)
            return;
        e.Cancel = true;
        closeWhenIdle = true;
        CancelTransfer();
    }

    private void UpdateControls()
    {
        bool idle = operation is null;
        bool online = archive is not null;
        signIn.Text = online ? "Sign out" : "Sign in";
        signIn.Enabled = idle;
        connection.Text = online ? "●  SFTP connected  ·  " + ArchiveSession.Host : "Disconnected";
        connection.ForeColor = online ? DarkTheme.Mint : DarkTheme.Muted;
        windows.SetAvailable(idle, windows.HasFolder);
        surf.SetAvailable(idle, online && surf.HasFolder);
        upload.Enabled = idle && surf.HasFolder && windows.SelectedFiles.Count > 0;
        download.Enabled = idle && windows.HasFolder && surf.SelectedFiles.Count > 0;
        probe.Enabled = idle && online && windows.SelectedFiles.Count == 1;
        status.Enabled = idle && surf.SelectedFiles.Count > 0;
        newFolder.Enabled = idle && surf.HasFolder;
        transfers.SetCancelEnabled(transferring && operation is { IsCancellationRequested: false });
    }

    private void SetFeedback(string text, bool error = false)
    {
        feedback.Text = text;
        feedback.ForeColor = error ? DarkTheme.Error : DarkTheme.Muted;
    }
}
