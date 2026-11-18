namespace SurfArchive;

internal sealed class MainWindow : Form
{
    private readonly BrowserPane windows = new("Archief / Windows", local: true);
    private readonly BrowserPane surf = new("SURF Data Archive", local: false);
    private readonly TransferLog transfers = new();
    private readonly Button signIn = DarkTheme.Button("Sign out");
    private readonly Button upload = DarkTheme.Button("Upload to SURF  →", primary: true);
    private readonly Button download = DarkTheme.Button("← Download", primary: true);
    private readonly Button restore = DarkTheme.Button("Restore from tape");
    private readonly Button package = DarkTheme.Button("Archive to SURF…", primary: true);
    private readonly Button resume = DarkTheme.Button("Resume archive upload…");
    private readonly Button removeWindows = DarkTheme.Button("Remove…");
    private readonly Button removeSurf = DarkTheme.Button("Remove…");
    private readonly Button newFolder = DarkTheme.Button("+ New folder");
    private readonly Label connection = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Mint);
    private readonly Label feedback = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private ArchiveSession? archive;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? statusLookup;
    private readonly Dictionary<string, (TransferJob Job, CancellationTokenSource Cancellation)> restores = new(StringComparer.Ordinal);
    private bool transferring;
    private bool closeWhenIdle;

    public MainWindow(ArchiveSession? session)
    {
        archive = session;
        DarkTheme.Window(this);
        Text = "SurfDataArchiveTransfer";
        MinimumSize = new Size(1040, 660);
        ClientSize = new Size(1410, 810);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();

        windows.AddLocation(LocalFiles.ArchiveDrive, LocalFiles.ArchiveDrive);
        windows.AddLocation(LocalFiles.BasicFolder, LocalFiles.BasicFolder);
        windows.AddLocation("Archief Active (L:)", LocalFiles.ActiveArchive);
        foreach (string drive in Directory.GetLogicalDrives())
        {
            if (!drive.Equals(LocalFiles.ArchiveDrive, StringComparison.OrdinalIgnoreCase))
                windows.AddLocation(drive, drive);
        }
        windows.SetHistory(new RecentFolders(null));
        if (archive is not null)
            surf.SetHistory(new RecentFolders(archive.Root));
        windows.Navigate += async (path, root) => await Run(_ => NavigateLocal(path, root));
        surf.Navigate += async (path, root) => await Run(_ => NavigateRemote(path, root));
        windows.NavigateRecent += async folder => await Run(_ => OpenRecent(folder, local: true));
        surf.NavigateRecent += async folder => await Run(_ => OpenRecent(folder, local: false));
        windows.OpenFile += async file => await OpenPreview(file, local: true);
        surf.OpenFile += async file => await OpenPreview(file, local: false);
        windows.SelectionChanged += UpdateControls;
        surf.SelectionChanged += UpdateControls;
        signIn.Click += async (_, _) => await Run(ToggleLogin);
        upload.Click += async (_, _) => await UploadDirect();
        download.Click += async (_, _) => await Run(token => Copy(toSurf: false, token), transfer: true);
        restore.Click += (_, _) => StartRestores();
        package.Click += async (_, _) => await ArchiveSelected();
        resume.Click += async (_, _) => await ResumeArchive();
        removeWindows.Click += async (_, _) => await ConfirmRemoval(local: true);
        removeSurf.Click += async (_, _) => await ConfirmRemoval(local: false);
        removeWindows.AccessibleName = "Remove selected Windows items";
        removeSurf.AccessibleName = "Remove selected SURF items";
        newFolder.Click += async (_, _) => await Run(MakeFolder);
        transfers.CancelRequested += CancelTransfer;
        transfers.SelectionChanged += UpdateControls;
        Shown += async (_, _) => await Run(InitialLoad);
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            StopStatusLookup();
            StopRestores();
            archive?.Dispose();
        };
        UpdateControls();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (int height in new[] { 46, -1, 43, 246, 25 })
            layout.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = DarkTheme.Panel,
            Padding = new Padding(15, 5, 12, 3), Margin = new Padding(0, 0, 0, 3)
        };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(DarkTheme.Row(resume), 0, 0);
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
        actions.Controls.Add(DarkTheme.Row(package, upload, removeWindows), 0, 0);
        actions.Controls.Add(DarkTheme.Row(download, restore, newFolder, removeSurf), 1, 0);
        layout.Controls.Add(actions, 0, 2);

        transfers.Margin = new Padding(13, 0, 13, 0);
        layout.Controls.Add(transfers, 0, 3);
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        feedback.Dock = DockStyle.Fill;
        feedback.Margin = new Padding(14, 4, 14, 0);
        layout.Controls.Add(feedback, 0, 4);
    }

    private async Task InitialLoad(CancellationToken token)
    {
        _ = Task.Run(FilePreview.PruneTemporaryFolders, token);
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
        foreach (var (path, label) in new[]
        {
            (LocalFiles.ArchiveDrive, LocalFiles.ArchiveDrive),
            (LocalFiles.ActiveArchive, "Archief Active")
        })
        {
            try
            {
                await NavigateLocal(path, label);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                transfers.Log($"Could not open {path}. {error.Message}");
            }
        }
        await NavigateLocal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents");
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
            _ = LoadArchiveStatuses(path, entries.ToArray());
        }
        catch
        {
            surf.RestoreLocation();
            throw;
        }
    }

    private async Task OpenRecent(RecentFolder folder, bool local)
    {
        BrowserPane pane = local ? windows : surf;
        string root;
        string[] parts;
        if (local)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.RootPath));
            string relative = Path.GetRelativePath(root, Path.GetFullPath(folder.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal))
                throw new IOException("The recent folder is outside its Windows location.");
            parts = relative == "." ? [] : relative.Split('\\', '/');
        }

        else
        {
            root = archive!.Root;
            if (folder.RootPath != root || (folder.Path != root && !folder.Path.StartsWith(root + "/", StringComparison.Ordinal)))
                throw new IOException("The recent folder is outside this SURF archive.");
            parts = folder.Path == root ? [] : folder.Path[(root.Length + 1)..].Split('/');
        }

        if (pane.HasKnownFolder(folder.Path))
        {
            if (local)
                await NavigateLocal(folder.Path, null);
            else
                await NavigateRemote(folder.Path, null);
            return;
        }

        var ancestors = new List<FolderListing>();
        string path = root;
        foreach (string part in parts)
        {
            var entries = await ReadFolder(path);
            var match = entries.FirstOrDefault(item => item.IsDirectory && item.Name.Equals(part,
                local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (match is null)
                throw new IOException("A folder on the saved path is missing or unavailable.");
            ancestors.Add(new(path, entries));
            path = local ? Path.Combine(path, match.Name) : ArchiveSession.Child(path, match.Name);
        }

        var content = await ReadFolder(path);
        pane.ShowFolder(path, folder.RootLabel, content, ancestors);
        if (!local)
            _ = LoadArchiveStatuses(path, content.ToArray());

        Task<List<ArchiveEntry>> ReadFolder(string directory) => local
            ? Task.Run(() => LocalFiles.List(directory))
            : Task.Run(() => archive!.List(directory));
    }

    private void StopStatusLookup()
    {
        var previous = statusLookup;
        statusLookup = null;
        previous?.Cancel();
    }

    private async Task LoadArchiveStatuses(string path, ArchiveEntry[] entries)
    {
        StopStatusLookup();
        using var lookup = new CancellationTokenSource();
        statusLookup = lookup;
        var session = archive!;
        try
        {
            var states = await Task.Run(() => session.ReadStatuses(path, entries, lookup.Token));
            if (statusLookup == lookup && surf.PathShown == path)
            {
                surf.ApplyStatuses(states, finish: true);
                UpdateControls();
            }
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (statusLookup == lookup && surf.PathShown == path)
            {
                surf.ApplyStatuses(new Dictionary<string, ArchiveState>(), finish: true);
                transfers.Log("Could not load archive statuses. " + error.Message);
                UpdateControls();
            }
        }
        finally
        {
            if (statusLookup == lookup)
                statusLookup = null;
        }
    }

    private async Task ToggleLogin(CancellationToken _)
    {
        if (archive is not null)
        {
            StopStatusLookup();
            StopRestores();
            var currentArchive = archive;
            archive = null;
            surf.SetHistory(null);
            surf.ClearFolder();
            await Task.Run(currentArchive.Dispose);
            SetFeedback("Signed out of SURF Data Archive.");
            return;
        }

        using var login = new LoginDialog { StartPosition = FormStartPosition.CenterParent };
        if (login.ShowDialog(this) != DialogResult.OK)
            return;

        archive = login.TakeSession();
        surf.SetHistory(new RecentFolders(archive.Root));
        await NavigateRemote(archive.Root, "My archive");
        SetFeedback("");
    }

    private async Task OpenPreview(ArchiveEntry file, bool local)
    {
        if (local)
        {
            string path = Path.Combine(windows.PathShown, file.Name);
            await Run(_ =>
            {
                FilePreview.Open(path);
                SetFeedback($"Opened {file.Name}.");
                return Task.CompletedTask;
            });
            return;
        }

        if (ArchiveStatus.CanRestore(file.Status))
        {
            SetFeedback($"{ArchiveStatus.Label(file.Status!.Value)}. Use Restore from tape, then double-click when ready.");
            return;
        }

        if (archive is null)
            return;
        string source = ArchiveSession.Child(surf.PathShown, file.Name);
        await Run(async token =>
        {
            string directory = FilePreview.CreateTemporaryFolder();
            var job = new TransferJob(file.Name, "Open preview", source, directory, file.Size);
            transfers.Add(job);
            try
            {
                job.SetPhase(TransferState.CheckingStatus);
                await Task.Run(() => archive.Download(source, directory, new Callback<TransferProgress>(job.Report), token));
                token.ThrowIfCancellationRequested();
                FilePreview.Open(Path.Combine(directory, file.Name));
                transfers.Log("Opened a temporary local copy.", job);
                transfers.Finish(job, TransferState.Completed);
                SetFeedback($"Opened a temporary copy of {file.Name}.");
            }
            catch (Exception error)
            {
                FilePreview.TryRemoveTemporaryFolder(directory);
                transfers.Log(error.Message, job);
                transfers.Finish(job, error is OperationCanceledException && token.IsCancellationRequested
                    ? TransferState.Cancelled : TransferState.Failed);
                throw;
            }
        }, transfer: true);
    }

    private async Task UploadDirect()
    {
        var selection = windows.SelectedItems;
        if (!LocalFiles.CanUploadDirectly(selection))
            return;
        if (MessageBox.Show(this,
            "Archive to SURF is recommended. It bundles and verifies your data and creates a contents list.\n\n" +
            $"Direct upload sends {selection.Count:N0} file(s) separately without creating an archive. Continue with direct upload?",
            "Direct upload to SURF", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK)
            await Run(token => Copy(toSurf: true, token), transfer: true);
    }

    private async Task Copy(bool toSurf, CancellationToken token)
    {
        if (!toSurf)
            StopStatusLookup();
        try
        {
            await CopySelection(toSurf, token);
        }
        finally
        {
            if (!toSurf && !closeWhenIdle && surf.HasFolder)
                _ = LoadArchiveStatuses(surf.PathShown, surf.SnapshotEntries());
        }
    }

    private async Task CopySelection(bool toSurf, CancellationToken token)
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
                    await Task.Run(() => archive!.Upload(source, target.PathShown, progress, token, LocalFiles.MinimumDirectUpload));
                else
                {
                    job.SetPhase(TransferState.CheckingStatus);
                    await Task.Run(() => archive!.Download(source, target.PathShown, progress, token));
                }
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

    private void StartRestores()
    {
        string directory = surf.PathShown;
        foreach (var item in surf.SelectedFiles.Where(item => ArchiveStatus.CanRestore(item.Status)))
        {
            string path = ArchiveSession.Child(directory, item.Name);
            if (restores.ContainsKey(path))
                continue;
            var job = new TransferJob(item.Name, "Tape restore", path, "SURF disk", 0, tape: true);
            var cancellation = new CancellationTokenSource();
            restores.Add(path, (job, cancellation));
            transfers.Add(job);
            _ = MonitorRestore(archive!, directory, path, job, cancellation);
        }
        UpdateControls();
    }

    private async Task MonitorRestore(ArchiveSession session, string directory, string path, TransferJob job,
        CancellationTokenSource cancellation)
    {
        var progress = new Progress<RestoreUpdate>(update =>
        {
            if (IsDisposed || cancellation.IsCancellationRequested ||
                !restores.TryGetValue(path, out var current) || current.Job != job)
                return;
            job.SetPhase(update.Phase);
            transfers.Log(update.Message, job);
            if (surf.PathShown == directory && update.State is { } state)
            {
                surf.ApplyStatuses(new Dictionary<string, ArchiveState>(StringComparer.Ordinal) { [job.Name] = state }, finish: false);
                UpdateControls();
            }
        });
        try
        {
            var state = await Task.Run(() => session.MonitorRestore(path, ((IProgress<RestoreUpdate>)progress).Report, cancellation.Token));
            if (!IsDisposed)
            {
                if (!cancellation.IsCancellationRequested && surf.PathShown == directory)
                    surf.ApplyStatuses(new Dictionary<string, ArchiveState>(StringComparer.Ordinal) { [job.Name] = state }, finish: false);
                transfers.Log("Ready. Select the file and click Download when you want to copy it to Windows.", job);
                transfers.Finish(job, TransferState.Ready);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsDisposed)
            {
                transfers.Log("Monitoring stopped. A restore already requested on SURF may still finish.", job);
                transfers.Finish(job, TransferState.Cancelled);
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                transfers.Log(error.Message, job);
                transfers.Finish(job, TransferState.Failed);
            }
        }
        finally
        {
            if (restores.TryGetValue(path, out var current) && current.Job == job)
                restores.Remove(path);
            cancellation.Dispose();
            if (!IsDisposed)
                UpdateControls();
        }
    }

    private void StopRestores()
    {
        foreach (var item in restores.Values)
            item.Cancellation.Cancel();
        restores.Clear();
    }

    private async Task ArchiveSelected()
    {
        var selection = windows.SelectedItems;
        string source = windows.PathShown;
        string destination = surf.PathShown;
        if (!ArchivePackage.CanArchive(selection))
            return;
        string? filesFolderName = null;
        if (!selection[0].IsDirectory)
        {
            filesFolderName = PackageDialog.AskFolderName(this);
            if (filesFolderName is null)
                return;
        }

        await Run(async token =>
        {
            SetFeedback("Scanning selected data and checking staging space…");
            var requests = await Task.Run(() => ArchivePackage.PlanSelection(source, destination, selection, filesFolderName, token));
            await Task.Run(() => archive!.CheckArchiveDestinations(requests, token));
            bool? removeOriginals = PackageDialog.Confirm(this, requests);
            if (removeOriginals is null)
            {
                SetFeedback("");
                return;
            }

            foreach (var request in requests)
            {
                token.ThrowIfCancellationRequested();
                await RunPackage(request.Name, request.LocalPath, request.RemotePath, (target, progress) =>
                    ArchivePackage.Create(request.Plan, request.LocalName, request.Name, request.Parent,
                        target, removeOriginals.Value, progress, token), token);
            }
            await NavigateLocal(source, null);
            await NavigateRemote(destination, null);
            SetFeedback(removeOriginals.Value
                ? "Archives uploaded and verified. Originals and local archive parts removed."
                : "Archives uploaded and verified. Originals kept.");
        }, transfer: true);
    }

    private async Task ResumeArchive()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the local SURF package folder containing package.json.",
            UseDescriptionForTitle = true, ShowNewFolderButton = false, SelectedPath = windows.PathShown
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        string folder = picker.SelectedPath;
        await Run(async token =>
        {
            var manifest = await Task.Run(() => ArchivePackage.ReadManifest(folder));
            string remote = ArchiveSession.Child(manifest.Destination, manifest.RemoteName);
            if (MessageBox.Show(this,
                $"Resume uploading to:\n{remote}\n\nLocal TAR parts will be removed after every SURF checksum matches.",
                "Resume archive upload", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                SetFeedback("");
                return;
            }
            await RunPackage(manifest.RemoteName, folder, remote, (target, progress) =>
                ArchivePackage.Resume(folder, target, progress, token), token);
            if (surf.HasFolder)
                await NavigateRemote(surf.PathShown, null);
            SetFeedback("Archive upload verified. Remaining local archive parts removed.");
        }, transfer: true);
    }

    private async Task RunPackage(string name, string recovery, string destination,
        Func<IPackageDestination, Action<PackageMeter>, Task> run, CancellationToken token)
    {
        var stages = new Dictionary<PackageStep, TransferJob>();
        var gate = new object();
        SetFeedback("Preparing archive destination…");
        try
        {
            await Task.Run(async () =>
            {
                using var target = archive!.PackageTarget();
                await run(target, Report);
            });
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            string message = Directory.Exists(recovery) ? $"Local package folder kept: {recovery}" : "No package folder was created.";
            foreach (var stage in stages.Values)
            {
                transfers.Log(message, stage);
                transfers.Finish(stage, cancelled ? TransferState.Cancelled : TransferState.Failed);
            }
            transfers.Log(message);
            if (cancelled)
                throw;
            throw new IOException($"{error.Message} {message}", error);
        }

        void Report(PackageMeter meter)
        {
            lock (gate)
            {
                if (!stages.TryGetValue(meter.Step, out var job))
                {
                    job = new TransferJob(name, StepCaption(meter.Step), recovery, destination, meter.Total, meter.CountItems);
                    stages.Add(meter.Step, job);
                    Invoke(() => transfers.Add(job));
                }
                job.Report(new(name, meter.Done, meter.Total));
                if (meter.Finished)
                {
                    Invoke(() => transfers.Finish(job, meter.Skipped ? TransferState.Skipped : TransferState.Completed));
                    stages.Remove(meter.Step);
                }
            }
        }
    }

    private static string StepCaption(PackageStep step) => step switch
    {
        PackageStep.Archive => "Compress TAR",
        PackageStep.Verify => "Verify local TAR",
        PackageStep.Index => "Write contents",
        PackageStep.RemoveOriginals => "Remove originals",
        PackageStep.Upload => "Upload parts",
        PackageStep.RemoteSha => "SURF SHA-256",
        _ => "Remove local parts"
    };

    private async Task ConfirmRemoval(bool local)
    {
        BrowserPane pane = local ? windows : surf;
        var items = pane.SelectedItems;
        string directory = pane.PathShown;
        if (items.Count == 0 || !RemovalDialog.Confirm(this, local, directory, items))
            return;

        await Run(async _ =>
        {
            int removed = 0;
            foreach (var item in items)
            {
                SetFeedback($"Removing {item.Name}…");
                try
                {
                    if (local)
                        await Task.Run(() => LocalFiles.Remove(directory, item));
                    else
                        await Task.Run(() => archive!.Remove(directory, item));
                    removed++;
                    transfers.Log($"Removed from {(local ? "Windows" : "SURF")}: {item.Name}");
                }
                catch (Exception error)
                {
                    transfers.Log($"Removal failed or incomplete for {item.Name}: {error.Message}");
                }
            }
            if (local)
                await NavigateLocal(directory, null);
            else
                await NavigateRemote(directory, null);
            int failed = items.Count - removed;
            string summary = $"Removed {removed:N0} of {items.Count:N0} selected items.";
            SetFeedback(failed == 0 ? summary : summary + " See the log for incomplete removals.", error: failed > 0);
        });
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
            SetFeedback("Activity cancelled.");
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
        if (operation is null && transfers.SelectedJob is { Tape: true } job)
        {
            foreach (var item in restores.Values.Where(item => item.Job == job))
                item.Cancellation.Cancel();
            UpdateControls();
            return;
        }
        if (operation is null || !transferring)
            return;
        operation.Cancel();
        UpdateControls();
        SetFeedback("Cancelling activity…");
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
        var localSelection = windows.SelectedItems;
        upload.Enabled = idle && online && surf.HasFolder && LocalFiles.CanUploadDirectly(localSelection);
        var selected = surf.SelectedFiles;
        download.Enabled = idle && online && windows.HasFolder && selected.Count > 0 &&
            selected.All(item => ArchiveStatus.IsOnDisk(item.Status));
        restore.Enabled = idle && online && selected.Any(item => ArchiveStatus.CanRestore(item.Status)
            && !restores.ContainsKey(ArchiveSession.Child(surf.PathShown, item.Name)));
        package.Enabled = idle && online && surf.HasFolder && ArchivePackage.CanArchive(localSelection);
        resume.Enabled = idle && online;
        removeWindows.Enabled = idle && windows.SelectedItems.Count > 0;
        removeSurf.Enabled = idle && online && surf.SelectedItems.Count > 0;
        newFolder.Enabled = idle && surf.HasFolder;
        bool monitoring = idle && transfers.SelectedJob is { Tape: true } job &&
            restores.Values.Any(item => item.Job == job && !item.Cancellation.IsCancellationRequested);
        transfers.SetCancel(monitoring ? "Stop monitoring" : "Cancel activity",
            monitoring || (transferring && operation is { IsCancellationRequested: false }));
    }

    private void SetFeedback(string text, bool error = false)
    {
        feedback.Text = text;
        feedback.ForeColor = error ? DarkTheme.Error : DarkTheme.Muted;
    }
}
