using System.Diagnostics;

namespace SurfArchive;

internal sealed class MainWindow : Form
{
    private readonly BrowserPane windows = new("Local / NAS", local: true);
    private readonly BrowserPane surf = new("SURF Data Archive", local: false);
    private readonly TransferLog transfers = new();
    private readonly ArchiveQueue archiveQueue = new();
    private readonly ArchiveQueuePanel queuePanel = new();
    private readonly RowStyle queueHeight = new(SizeType.Absolute, ArchiveQueuePanel.EmptyHeight);
    private readonly RowStyle activityHeight = new(SizeType.Absolute, TransferLog.DisplayHeight);
    private readonly Button signIn = DarkTheme.Button("Sign out", ActionIcon.SignOut);
    private readonly Button upload = DarkTheme.Button("Upload to SURF", ActionIcon.Upload, primary: true);
    private readonly Button download = DarkTheme.Button("Download", ActionIcon.Download, primary: true);
    private readonly Button restore = DarkTheme.Button("Restore from tape", ActionIcon.Tape);
    private readonly Button package = DarkTheme.Button("Archive to SURF…", ActionIcon.Archive, primary: true);
    private readonly Button removeWindows = DarkTheme.Button("Remove…", ActionIcon.Trash);
    private readonly Button removeSurf = DarkTheme.Button("Remove…", ActionIcon.Trash);
    private readonly Button newFolder = DarkTheme.Button("New folder", ActionIcon.FolderPlus);
    private readonly Label connection = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Mint);
    private readonly Label feedback = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly Button cancelScan = DarkTheme.Button("Cancel scan", ActionIcon.Cancel);
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 200 };
    private readonly List<ActiveArchiveScan> archiveScans = [];
    private ArchiveSession? archive;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? archiveOperation;
    private CancellationTokenSource? statusLookup;
    private readonly Dictionary<string, RestoreActivity> restores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim retentionEditor = new(1);
    private string[] copySurfPaths = [];
    private string? packageSurfPath;
    private ArchiveRequest? pendingSourceRefresh;
    private int localListingVersion;
    private int surfListingVersion;
    private int localBrowsing;
    private int surfBrowsing;
    private bool transferring;
    private bool editingSurf;
    private bool closeWhenIdle;
    private string feedbackMessage = "";
    private bool feedbackIsError;

    public MainWindow(ArchiveSession? session)
    {
        archive = session;
        DarkTheme.Window(this);
        Text = "SurfDataArchiveTransfer";
        MinimumSize = new Size(1040, 660);
        ClientSize = new Size(1410, 850);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        scanTimer.Tick += (_, _) => RefreshScanStatus();
        cancelScan.Click += (_, _) =>
        {
            if (archiveScans.LastOrDefault() is { } scan && scan.Clock.Elapsed >= TimeSpan.FromSeconds(10))
                scan.Cancellation.Cancel();
            RefreshScanStatus();
        };
        RefreshScanStatus();

        windows.AddLocation(LocalFiles.ArchiveDrive, LocalFiles.ArchiveDrive);
        windows.AddLocation(LocalFiles.BasicFolder, LocalFiles.BasicFolder);
        windows.AddLocation(LocalFiles.ActiveArchive, LocalFiles.ActiveArchive);
        foreach (string drive in Directory.GetLogicalDrives())
        {
            if (!drive.Equals(LocalFiles.ArchiveDrive, StringComparison.OrdinalIgnoreCase))
                windows.AddLocation(drive, drive);
        }
        windows.SetHistory(new RecentFolders(null));
        if (archive is not null)
            surf.SetHistory(new RecentFolders(archive.Root));
        windows.Navigate += async (path, root) => await Browse(local: true, () => NavigateLocal(path, root));
        surf.Navigate += async (path, root) => await Browse(local: false, () => NavigateRemote(path, root));
        windows.NavigateRecent += async folder => await Browse(local: true, () => OpenRecent(folder, local: true));
        surf.NavigateRecent += async folder => await Browse(local: false, () => OpenRecent(folder, local: false));
        windows.OpenFile += file => OpenPreview(file, local: true);
        surf.OpenFile += file => OpenPreview(file, local: false);
        windows.SelectionChanged += UpdateControls;
        surf.SelectionChanged += UpdateControls;
        signIn.Click += async (_, _) => await Run(ToggleLogin);
        upload.Click += async (_, _) => await UploadDirect();
        download.Click += async (_, _) => await StartCopy(CaptureCopy(toSurf: false));
        restore.Click += (_, _) => StartRestores();
        package.Click += async (_, _) => await ArchiveSelected();
        removeWindows.Click += async (_, _) => await ConfirmRemoval(local: true);
        removeSurf.Click += async (_, _) => await ConfirmRemoval(local: false);
        removeWindows.AccessibleName = "Remove selected Local / NAS items";
        removeSurf.AccessibleName = "Remove selected SURF items";
        newFolder.Click += async (_, _) => await MakeFolder();
        transfers.CancelRequested += CancelSelectedActivity;
        transfers.SelectionChanged += UpdateControls;
        transfers.HorizontalScrollbarChanged += (_, _) => SchedulePanelResize();
        queuePanel.HorizontalScrollbarChanged += (_, _) => SchedulePanelResize();
        SizeChanged += (_, _) => SchedulePanelResize();
        archiveQueue.Changed += () =>
        {
            queuePanel.ShowItems(archiveQueue.Items);
            FitActivityPanels();
        };
        queuePanel.TogglePause += item =>
        {
            if (ChangeQueue(() => archiveQueue.TogglePause(item)))
                TryStartNextArchive();
        };
        queuePanel.RemoveItem += item => ChangeQueue(() => archiveQueue.Remove(item));
        queuePanel.RemoveFinished += () => ChangeQueue(archiveQueue.RemoveFinished);
        queuePanel.ResumeUpload += async item => await ResumeArchive(item);
        queuePanel.BrowseRecovery += async () => await ResumeArchive();
        Shown += async (_, _) => await Run(InitialLoad);
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            scanTimer.Dispose();
            StopStatusLookup();
            StopRestores();
            archiveQueue.Dispose();
            archive?.Dispose();
        };
        UpdateControls();
    }

    private void SchedulePanelResize()
    {
        // Scrollbar visibility can change while the grid is laying out its rows.
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(FitActivityPanels);
    }

    private void FitActivityPanels()
    {
        if (IsDisposed)
            return;
        float scale = DeviceDpi / 96f;
        float queue = (float)Math.Round(ArchiveQueuePanel.HeightFor(archiveQueue.Items.Count) * scale) +
            queuePanel.HorizontalScrollbarHeight;
        float activity = (float)Math.Round(TransferLog.DisplayHeight * scale) + transfers.HorizontalScrollbarHeight;
        if (queueHeight.Height == queue && activityHeight.Height == activity)
            return;
        queueHeight.Height = queue;
        activityHeight.Height = activity;
        transfers.Parent?.PerformLayout();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        foreach (int height in new[] { 38, -1, 35 })
            layout.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        layout.RowStyles.Add(queueHeight);
        layout.RowStyles.Add(activityHeight);
        layout.RowStyles.Add(new(SizeType.Absolute, 32));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = DarkTheme.Panel,
            Padding = new Padding(15, 5, 12, 3), Margin = new Padding(0, 0, 0, 3)
        };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        connection.Margin = new Padding(0, 5, 17, 0);
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

        queuePanel.Margin = new Padding(13, 0, 13, 4);
        layout.Controls.Add(queuePanel, 0, 3);
        transfers.Margin = new Padding(13, 0, 13, 0);
        layout.Controls.Add(transfers, 0, 4);
        var statusBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(14, 2, 13, 2) };
        statusBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        statusBar.ColumnStyles.Add(new(SizeType.AutoSize));
        statusBar.RowStyles.Add(new(SizeType.Percent, 100));
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        feedback.UseMnemonic = false;
        feedback.TextAlign = ContentAlignment.MiddleLeft;
        feedback.Dock = DockStyle.Fill;
        feedback.Margin = Padding.Empty;
        statusBar.Controls.Add(feedback, 0, 0);
        statusBar.Controls.Add(cancelScan, 1, 0);
        layout.Controls.Add(statusBar, 0, 5);
    }

    private async Task<T> ScanArchive<T>(CancellationTokenSource cancellation, string phase,
        Func<ArchiveScanProgress, CancellationToken, T> scan)
    {
        var active = new ActiveArchiveScan(cancellation, new(phase));
        archiveScans.Add(active);
        RefreshScanStatus();
        try { return await Task.Run(() => scan(active.Progress, cancellation.Token), cancellation.Token); }
        finally
        {
            archiveScans.Remove(active);
            RefreshScanStatus();
        }
    }

    private void RefreshScanStatus()
    {
        var active = archiveScans.LastOrDefault();
        scanTimer.Enabled = active is not null;
        cancelScan.Visible = active is not null && active.Clock.Elapsed >= TimeSpan.FromSeconds(10);
        cancelScan.Enabled = active is not null && !active.Cancellation.IsCancellationRequested;
        if (active is null)
        {
            feedback.Text = feedbackMessage;
            feedback.ForeColor = feedbackIsError ? DarkTheme.Error : DarkTheme.Muted;
            return;
        }
        var (phase, files, folders, bytes) = active.Progress.Read();
        string label = active.Cancellation.IsCancellationRequested ? "Cancelling scan…" : phase;
        if (label.Length > 52)
            label = label[..51] + "…";
        feedback.ForeColor = DarkTheme.Mint;
        feedback.Text = $"{label}  ·  {files + folders:N0} items ({files:N0} files, {folders:N0} folders)" +
            $"  ·  {TransferJob.FormatBytes(bytes)}  ·  {TransferJob.FormatTime(active.Clock.Elapsed)}";
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
        await Task.WhenAll(local, NavigateRemote(archive.Root, "My archive"), LoadArchiveQueue(archive.Root, token));
    }

    private async Task LoadDefaultLocal()
    {
        foreach (var (path, label) in new[]
        {
            (LocalFiles.ArchiveDrive, LocalFiles.ArchiveDrive),
            (LocalFiles.ActiveArchive, LocalFiles.ActiveArchive)
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
        int version = ++localListingVersion;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var entries = await Task.Run(() => LocalFiles.List(full));
            if (!closeWhenIdle && version == localListingVersion)
            {
                windows.ShowFolder(full, root, entries);
                UpdateControls();
            }
        }
        catch when (closeWhenIdle || version != localListingVersion) { }
        catch
        {
            if (!closeWhenIdle && version == localListingVersion)
                windows.RestoreLocation();
            throw;
        }
    }

    private async Task NavigateRemote(string path, string? root)
    {
        var session = archive!;
        int version = ++surfListingVersion;
        try
        {
            var entries = await Task.Run(() => session.ListIndependent(path));
            if (version != surfListingVersion || session != archive || closeWhenIdle)
                return;
            surf.ShowFolder(path, root, entries);
            UpdateControls();
            _ = LoadArchiveStatuses(path, entries.ToArray());
        }
        catch when (closeWhenIdle || version != surfListingVersion || session != archive) { }
        catch
        {
            if (!closeWhenIdle && version == surfListingVersion && session == archive)
                surf.RestoreLocation();
            throw;
        }
    }

    private async Task OpenRecent(RecentFolder folder, bool local)
    {
        int version = local ? ++localListingVersion : ++surfListingVersion;
        var session = archive;
        BrowserPane pane = local ? windows : surf;
        string root;
        string[] parts;
        if (local)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.RootPath));
            string relative = Path.GetRelativePath(root, Path.GetFullPath(folder.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal))
                throw new IOException("The recent folder is outside its Local / NAS location.");
            parts = relative == "." ? [] : relative.Split('\\', '/');
        }

        else
        {
            root = session!.Root;
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

        try
        {
            var result = local
                ? await ReadRecent(directory => Task.Run(() => LocalFiles.List(directory)))
                : await session!.WithIndependentListing(ReadRecent);
            if (!IsCurrent() || result is not { } listing)
                return;
            pane.ShowFolder(listing.Path, folder.RootLabel, listing.Content, listing.Ancestors);
            UpdateControls();
            if (!local)
                _ = LoadArchiveStatuses(listing.Path, listing.Content.ToArray());
        }
        catch when (!IsCurrent()) { }

        bool IsCurrent() => !closeWhenIdle &&
            (local ? version == localListingVersion : version == surfListingVersion && session == archive);

        async Task<(string Path, List<ArchiveEntry> Content, List<FolderListing> Ancestors)?> ReadRecent(
            Func<string, Task<List<ArchiveEntry>>> list)
        {
            var ancestors = new List<FolderListing>();
            string path = root;
            foreach (string part in parts)
            {
                if (!IsCurrent())
                    return null;
                var entries = await list(path);
                var match = entries.FirstOrDefault(item => item.IsDirectory && item.Name.Equals(part,
                    local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (match is null)
                    throw new IOException("A folder on the saved path is missing or unavailable.");
                ancestors.Add(new(path, entries));
                path = local ? Path.Combine(path, match.Name) : ArchiveSession.Child(path, match.Name);
            }
            return IsCurrent() ? (path, await list(path), ancestors) : null;
        }
    }

    private bool CanBrowse => !closeWhenIdle && (operation is null || transferring);

    private async Task Browse(bool local, Func<Task> load)
    {
        if (!CanBrowse || (!local && archive is null))
            return;
        if (local)
            localBrowsing++;
        else
            surfBrowsing++;
        try { await load(); }
        catch (Exception error)
        {
            if (!closeWhenIdle)
            {
                transfers.Log(error.Message);
                SetFeedback(error.Message, error: true);
            }
        }
        finally
        {
            if (local)
                localBrowsing--;
            else
                surfBrowsing--;
        }
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
            IProgress<FolderArchiveStatus> folders = new Progress<FolderArchiveStatus>(status =>
            {
                if (statusLookup == lookup && surf.PathShown == path)
                {
                    surf.ApplyFolderStatus(status);
                    UpdateControls();
                }
            });
            await Task.Run(() => session.ReadFolderStatuses(path, entries, folders, lookup.Token));
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (statusLookup == lookup && surf.PathShown == path)
            {
                surf.ApplyStatuses(new Dictionary<string, ArchiveState>(), finish: true);
                foreach (var entry in surf.SnapshotEntries().Where(item => item.IsDirectory && item.Status is null))
                    surf.ApplyFolderStatus(new(entry.Name, ArchiveState.Unknown, null));
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

    private async Task ToggleLogin(CancellationToken token)
    {
        if (archive is not null)
        {
            archiveQueue.Detach();
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
        SetFeedback("");
        await Task.WhenAll(NavigateRemote(archive.Root, "My archive"), LoadArchiveQueue(archive.Root, token));
    }

    private async Task LoadArchiveQueue(string root, CancellationToken token)
    {
        ArchiveQueueStore? store = null;
        try
        {
            store = new ArchiveQueueStore(root);
            var saved = await Task.Run(store.Read, token);
            token.ThrowIfCancellationRequested();
            archiveQueue.Attach(store, saved);
            store = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException)
        {
            transfers.Log($"Could not load the archive queue. {error.Message}");
            SetFeedback(error.Message, error: true);
        }
        finally { store?.Dispose(); }
    }

    private void OpenPreview(ArchiveEntry file, bool local)
    {
        if (!CanBrowse)
            return;
        if (!local)
        {
            QueueRestore(surf.PathShown, file, openWhenReady: true);
            return;
        }
        try
        {
            FilePreview.Open(Path.Combine(windows.PathShown, file.Name));
            SetFeedback($"Opened {file.Name}.");
        }
        catch (Exception error)
        {
            transfers.Log(error.Message);
            SetFeedback(error.Message, error: true);
        }
    }

    private async Task UploadDirect()
    {
        var copy = CaptureCopy(toSurf: true);
        if (!LocalFiles.CanUploadDirectly(copy.Items))
            return;
        if (MessageBox.Show(this,
            "Archive to SURF is recommended. It bundles and verifies your data and creates a contents list.\n\n" +
            $"Direct upload sends {copy.Items.Count:N0} file(s) separately without creating an archive. Continue with direct upload?",
            "Direct upload to SURF", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK)
            await StartCopy(copy);
    }

    private DirectCopy CaptureCopy(bool toSurf) => new(toSurf, windows.PathShown, surf.PathShown,
        toSurf ? windows.SelectedItems : surf.SelectedItems);

    private Task StartCopy(DirectCopy copy) => archive is { } session && copy.Items.Count > 0 &&
        operation is null && archiveOperation is null && !editingSurf && !closeWhenIdle
        ? Run(token => Copy(session, copy, token), transfer: true)
        : Task.CompletedTask;

    private async Task Copy(ArchiveSession session, DirectCopy copy, CancellationToken token)
    {
        copySurfPaths = copy.Items.Select(item => ArchiveSession.Child(copy.SurfDirectory, item.Name)).ToArray();
        UpdateControls();
        try
        {
            await CopySelection(session, copy, token);
        }
        finally
        {
            copySurfPaths = [];
            if (!copy.ToSurf && !closeWhenIdle && surfBrowsing == 0 && surf.PathShown == copy.SurfDirectory)
                _ = LoadArchiveStatuses(surf.PathShown, surf.SnapshotEntries());
        }
    }

    private async Task CopySelection(ArchiveSession session, DirectCopy copy, CancellationToken token)
    {
        bool toSurf = copy.ToSurf;
        string destination = toSurf ? copy.SurfDirectory : copy.LocalDirectory;
        int failures = 0, waitingForTape = 0;
        foreach (var entry in copy.Items)
        {
            token.ThrowIfCancellationRequested();
            string source = toSurf ? Path.Combine(copy.LocalDirectory, entry.Name) : ArchiveSession.Child(copy.SurfDirectory, entry.Name);
            string action = toSurf ? "Upload →" : entry.IsDirectory ? "Download folder" : "← Download";
            var job = new TransferJob(entry.Name, action, source, destination, entry.IsDirectory ? 0 : entry.Size);
            transfers.Add(job);

            try
            {
                var progress = new Callback<TransferProgress>(job.Report);
                if (toSurf)
                    await Task.Run(() => session.Upload(source, destination, progress, token, LocalFiles.MinimumDirectUpload));
                else if (entry.IsDirectory)
                {
                    job.SetPhase(TransferState.CheckingStatus);
                    var folder = await Task.Run(() => session.ScanFolder(source, entry.Name, token));
                    folder.Destination(destination);
                    int offline = await Task.Run(() => session.CountFolderFilesToRestore(folder, token));
                    if (offline > 0)
                    {
                        if (surf.PathShown == copy.SurfDirectory)
                            surf.ApplyFolderStatus(new(entry.Name, ArchiveState.OFL, false));
                        waitingForTape++;
                        transfers.Log($"{offline:N0} of {folder.Files.Count:N0} files need a disk copy. Download the folder once it is ready.", job);
                        if (!restores.ContainsKey(source) && MessageBox.Show(this,
                            $"Restore {offline:N0} file(s) in '{entry.Name}' to disk on SURF?\n\n" +
                            "When Activity shows Ready to download, select the folder and click Download again.",
                            "Restore folder from tape", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                            QueueRestore(copy.SurfDirectory, entry, openWhenReady: false, folder);
                        transfers.Finish(job, TransferState.Skipped);
                        continue;
                    }
                    await Task.Run(() => session.DownloadFolder(folder, destination, progress, token));
                }
                else
                {
                    job.SetPhase(TransferState.CheckingStatus);
                    await Task.Run(() => session.Download(source, destination, progress, token));
                }
                transfers.Log(entry.IsDirectory ? "Folder copied. All file sizes verified." : "Copy complete. File size verified.", job);
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

        if (toSurf && !editingSurf && !closeWhenIdle && surfBrowsing == 0 && surf.PathShown == destination)
            await NavigateRemote(destination, null);
        else if (!toSurf && !closeWhenIdle && localBrowsing == 0 &&
            windows.PathShown.Equals(destination, StringComparison.OrdinalIgnoreCase))
            await NavigateLocal(destination, null);
        SetFeedback(failures > 0 ? $"{failures:N0} transfer(s) failed. See Activity."
            : waitingForTape > 0 ? "Folders with files on tape can be downloaded once all files are on disk."
            : "Transfer complete.", failures > 0);
    }

    private void StartRestores()
    {
        string directory = surf.PathShown;
        foreach (var item in surf.SelectedItems.Where(item =>
            item.IsDirectory ? item.HasSubfolders != true && !ArchiveStatus.IsOnDisk(item.Status) : ArchiveStatus.CanRestore(item.Status)))
            QueueRestore(directory, item, openWhenReady: false);
        UpdateControls();
    }

    private void QueueRestore(string directory, ArchiveEntry item, bool openWhenReady, FolderDownload? folder = null)
    {
        if (archive is not { } session)
            return;
        string previewAction = RetentionMetadata.IsFileName(item.Name) ? "Open retention"
            : ContentsPreview.IsContents(item.Name) ? "Open contents" : "Open preview";
        string path;
        try { path = ArchiveSession.Child(directory, item.Name); }
        catch (ArgumentException error)
        {
            SetFeedback(error.Message, error: true);
            return;
        }
        if (restores.TryGetValue(path, out var existing))
        {
            if (!openWhenReady)
                return;
            if (existing.Cancellation.IsCancellationRequested)
            {
                SetFeedback("The previous activity is stopping. Try again when it has stopped.");
                return;
            }
            if (existing.Polling.RequestPreview())
            {
                existing.Job.Direction = previewAction;
                existing.Job.Notes[1] = "Destination: Temporary local preview";
                transfers.Log("Will open automatically when the file is on disk. Checking every 15 seconds.", existing.Job);
            }
            SetFeedback($"Will open {item.Name} when it is ready.");
            UpdateControls();
            return;
        }

        string target = openWhenReady ? "Temporary local preview" : "SURF disk";
        string action = openWhenReady ? previewAction : item.IsDirectory ? "Restore folder" : "Tape restore";
        var job = new TransferJob(item.Name, action, path, target, item.IsDirectory ? 0 : item.Size, tape: true);
        var activity = new RestoreActivity(job, new RestorePolling(), new CancellationTokenSource());
        if (openWhenReady)
            activity.Polling.RequestPreview();
        restores.Add(path, activity);
        transfers.Add(job);
        if (openWhenReady)
            SetFeedback($"Checking the SURF status of {item.Name}…");
        _ = item.IsDirectory ? MonitorFolderRestore(session, directory, path, activity, folder)
            : MonitorRestore(session, directory, path, activity);
        UpdateControls();
    }

    private async Task MonitorFolderRestore(ArchiveSession session, string directory, string path, RestoreActivity activity,
        FolderDownload? folder)
    {
        var job = activity.Job;
        var token = activity.Cancellation.Token;
        job.SetPhase(TransferState.CheckingStatus);
        bool finished = false;
        IProgress<RestoreUpdate> progress = new Progress<RestoreUpdate>(update =>
        {
            if (!finished && !IsDisposed && !token.IsCancellationRequested)
            {
                job.SetPhase(update.Phase);
                transfers.Log(update.Message, job);
            }
        });
        try
        {
            folder ??= await Task.Run(() => session.ScanFolder(path, job.Name, token));
            await Task.Run(() => session.RestoreFolder(folder, progress.Report, token));
            token.ThrowIfCancellationRequested();
            finished = true;
            if (IsDisposed)
                return;
            transfers.Log("All files are on disk. Select the folder and click Download.", job);
            transfers.Finish(job, TransferState.Ready);
            if (surf.PathShown == directory)
            {
                surf.ApplyFolderStatus(new(job.Name, ArchiveState.REG, false));
                _ = LoadArchiveStatuses(directory, surf.SnapshotEntries());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!IsDisposed)
            {
                transfers.Log("Monitoring stopped. Restore requests already sent to SURF may still finish.", job);
                transfers.Finish(job, TransferState.Cancelled);
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                transfers.Log(error.Message, job);
                transfers.Finish(job, TransferState.Failed);
                SetFeedback(error.Message, error: true);
            }
        }
        finally
        {
            finished = true;
            FinishRestore(path, activity);
        }
    }

    private async Task MonitorRestore(ArchiveSession session, string directory, string path, RestoreActivity activity)
    {
        TransferJob job = activity.Job;
        CancellationToken token = activity.Cancellation.Token;
        string? local = null;
        bool opened = false;
        try
        {
            await WaitForDisk(path, job.Name);
            if (IsDisposed)
                return;

            if (activity.Polling.OpenWhenReady)
            {
                bool editRetention = RetentionMetadata.IsFileName(job.Name);
                bool openContents = ContentsPreview.IsContents(job.Name);
                local = FilePreview.CreateTemporaryFolder();
                transfers.Log($"Downloading a temporary copy of {job.Name}.", job);
                await Task.Run(() => session.DownloadPreview(path, local, new Callback<TransferProgress>(job.Report), token,
                    editRetention ? RetentionMetadata.MaximumFileSize : null));
                token.ThrowIfCancellationRequested();
                if (IsDisposed)
                    return;
                string localPath = Path.Combine(local, job.Name);
                if (editRetention)
                {
                    await OpenRetention(session, directory, path, localPath, job, token);
                    return;
                }
                if (openContents)
                {
                    transfers.Log("Checking the current retention.json before opening contents.html.", job);
                    var (metadata, status) = await ReadCompanionRetention(local);
                    bool rendered = await Task.Run(() => ContentsPreview.AddRetention(localPath, metadata, status, token));
                    if (!rendered && !IsDisposed)
                        transfers.Log("This contents.html format does not support the retention preview.", job);
                }
                token.ThrowIfCancellationRequested();
                if (IsDisposed)
                    return;
                FilePreview.Open(localPath);
                opened = true;
                transfers.Log("Opened the temporary local copy.", job);
                transfers.Finish(job, TransferState.Completed);
                if (operation is null)
                    SetFeedback($"Opened a temporary copy of {job.Name}.");
            }
            else
            {
                transfers.Log("Ready. Select the file and click Download to copy it to the current Local / NAS folder.", job);
                transfers.Finish(job, TransferState.Ready);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!IsDisposed)
            {
                transfers.Log("Activity stopped. A restore already requested on SURF may still finish.", job);
                transfers.Finish(job, TransferState.Cancelled);
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                transfers.Log(error.Message, job);
                transfers.Finish(job, TransferState.Failed);
                if (activity.Polling.OpenWhenReady && operation is null)
                    SetFeedback(error.Message, error: true);
            }
        }
        finally
        {
            if (!opened && local is not null)
                FilePreview.TryRemoveTemporaryFolder(local);
            FinishRestore(path, activity);
        }

        async Task WaitForDisk(string remotePath, string name)
        {
            bool checking = true;
            IProgress<RestoreUpdate> progress = new Progress<RestoreUpdate>(update =>
            {
                if (!checking || IsDisposed || token.IsCancellationRequested)
                    return;
                job.SetPhase(update.Phase);
                transfers.Log($"{name} · {update.Message}", job);
                if (update.State is { } state)
                    ApplyStatus(name, state);
            });
            try
            {
                var state = await Task.Run(() => session.MonitorRestore(remotePath, progress.Report,
                    activity.Polling.WaitForNextCheck, token));
                token.ThrowIfCancellationRequested();
                ApplyStatus(name, state);
            }
            finally
            {
                checking = false;
            }
        }

        void ApplyStatus(string name, ArchiveState state)
        {
            if (IsDisposed || surf.PathShown != directory)
                return;
            surf.ApplyStatuses(new Dictionary<string, ArchiveState>(StringComparer.Ordinal) { [name] = state }, finish: false);
            UpdateControls();
        }

        async Task<(RetentionMetadata? Metadata, string Status)> ReadCompanionRetention(string localFolder)
        {
            const string name = RetentionMetadata.FileName;
            string remotePath = ArchiveSession.Child(directory, name);
            string localPath = Path.Combine(localFolder, name);
            job.SetPhase(TransferState.CheckingStatus);
            if (operation is null)
                SetFeedback("Checking retention.json before opening contents.html…");
            try
            {
                bool exists = await Task.Run(() => session.HasRetentionForPreview(remotePath, token));
                token.ThrowIfCancellationRequested();
                if (!exists)
                {
                    const string missing = "This SURF folder has no retention.json.";
                    transfers.Log(missing, job);
                    return (null, missing);
                }

                await WaitForDisk(remotePath, name);
                var metadata = await Task.Run(async () =>
                {
                    await session.DownloadPreview(remotePath, localFolder, new Callback<TransferProgress>(_ => { }), token,
                        RetentionMetadata.MaximumFileSize);
                    return RetentionMetadata.Read(localPath).Metadata;
                });
                token.ThrowIfCancellationRequested();
                transfers.Log("Read the current retention.json from the same SURF folder.", job);
                return (metadata, "Read from retention.json in this SURF folder when the preview was opened.");
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                if (!IsDisposed)
                    transfers.Log($"Could not read retention.json. {error.Message}", job);
                return (null, "The current retention.json could not be read. See Activity in SurfDataArchiveTransfer.");
            }
            finally
            {
                File.Delete(localPath);
            }
        }
    }

    private void FinishRestore(string path, RestoreActivity activity)
    {
        if (restores.TryGetValue(path, out var current) && current == activity)
            restores.Remove(path);
        activity.Cancellation.Dispose();
        if (!IsDisposed)
            UpdateControls();
    }

    private async Task OpenRetention(ArchiveSession session, string directory, string path, string localPath,
        TransferJob job, CancellationToken token)
    {
        var (metadata, originalSha) = RetentionMetadata.Read(localPath);
        await retentionEditor.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            transfers.Log("Opened a copy in the retention editor.", job);
            transfers.Finish(job, TransferState.Completed);
            bool saved = PackageDialog.EditRetention(this, path, metadata, Save, token);
            if (saved && operation is null && !IsDisposed)
                SetFeedback("Retention saved on SURF.");
        }
        finally
        {
            retentionEditor.Release();
        }

        async Task Save(byte[] payload)
        {
            var saveJob = new TransferJob(job.Name, "Save retention", "Retention editor", path, payload.Length,
                working: "Uploading");
            transfers.Add(saveJob);
            try
            {
                await Task.Run(() => session.SaveRetention(directory, job.Name, originalSha, payload,
                    new Callback<TransferProgress>(saveJob.Report), token));
                if (!IsDisposed)
                {
                    transfers.Log("The SHA-256 matched. The retention file was updated on SURF.", saveJob);
                    transfers.Finish(saveJob, TransferState.Completed);
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    transfers.Log(error.Message, saveJob);
                    transfers.Finish(saveJob, token.IsCancellationRequested ? TransferState.Cancelled : TransferState.Failed);
                }
                throw;
            }
        }
    }

    private void StopRestores()
    {
        var pending = restores.Values.ToArray();
        restores.Clear();
        foreach (var item in pending)
            item.Cancellation.Cancel();
    }

    private async Task ArchiveSelected()
    {
        if (operation is not null || archive is null || !archiveQueue.IsAvailable || !surf.HasFolder || closeWhenIdle)
            return;
        var selection = windows.SelectedItems;
        string source = windows.PathShown;
        string destination = surf.PathShown;
        if (!ArchivePackage.CanArchive(selection))
            return;
        await Run(async token =>
        {
            string? filesFolderName = null;
            if (!selection[0].IsDirectory)
            {
                filesFolderName = PackageDialog.AskFolderName(this);
                if (filesFolderName is null)
                    return;
            }
            var cancellation = operation!;
            var requests = await ScanArchive(cancellation, "Scanning selection", (progress, ct) =>
                ArchivePackage.PlanSelection(source, destination, selection, filesFolderName, ct, progress));
            archiveQueue.CheckDestinations(requests.Select(request => request.RemotePath));
            var existing = surf.SnapshotEntries().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var request in requests)
            {
                if (existing.Contains(request.Name))
                    throw new IOException($"The SURF destination already exists at {request.RemotePath}. Choose a different parent folder or name.");
            }
            var options = PackageDialog.Confirm(this, requests);
            if (options is null)
            {
                SetFeedback("");
                return;
            }
            token.ThrowIfCancellationRequested();
            var queued = await ScanArchive(cancellation, "Preparing the archive queue", (progress, ct) => requests.Select(request =>
                ArchiveQueueItem.FromRequest(request, options, ct, progress)).ToArray());
            token.ThrowIfCancellationRequested();
            archiveQueue.Add(queued);
            SetFeedback(requests.Count == 1 ? "Archive added to the queue." : $"{requests.Count:N0} archives added to the queue.");
        });
    }

    private void TryStartNextArchive()
    {
        if (archiveOperation is not null || operation is not null || editingSurf || archive is null || closeWhenIdle)
            return;
        ArchiveQueueItem? item = null;
        if (ChangeQueue(() => item = archiveQueue.StartNext()) && item is not null)
            _ = RunQueuedArchive(item);
    }

    private bool ChangeQueue(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string message = $"Could not save the archive queue. {error.Message}";
            transfers.Log(message);
            SetFeedback(message, error: true);
            return false;
        }
    }

    private async Task RunQueuedArchive(ArchiveQueueItem item)
    {
        using var cancellation = new CancellationTokenSource();
        archiveOperation = cancellation;
        packageSurfPath = item.RemotePath;
        var token = cancellation.Token;
        UpdateControls();
        ArchiveQueueState outcome = ArchiveQueueState.Failed;
        string? detail = null;
        ArchiveRequest? request = null;
        var options = item.Options with
        {
            Retention = item.Options.Retention with { ArchivedOn = DateOnly.FromDateTime(DateTime.Today) }
        };
        try
        {
            try
            {
                request = await ScanArchive(cancellation, $"Rescanning {item.Name}", (progress, ct) => item.Prepare(ct, progress));
                token.ThrowIfCancellationRequested();
                archiveQueue.SetRecoveryPath(item, request.LocalPath);
                if (options.LocalStagingFolder is not null && request.Plan.UsesFallback)
                    transfers.Log($"Local staging has too little free space for {request.Name}. Using {request.LocalPath}.");
                await RunPackage(request.Name, request.LocalPath, request.RemotePath, (target, progress) =>
                    ArchivePackage.Create(request.Plan, request.LocalName, request.Name, request.Parent,
                        target, options, progress, token), token, item.LocalName);
                outcome = ArchiveQueueState.Finished;
                transfers.Log($"{item.Name} was uploaded and verified.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                outcome = closeWhenIdle ? ArchiveQueueState.Interrupted : ArchiveQueueState.Cancelled;
                detail = closeWhenIdle ? "The app closed while this archive was running." : "Archive cancelled.";
                transfers.Log($"{item.Name}. {detail}");
                if (operation is null)
                    SetFeedback(detail);
            }
            catch (Exception error)
            {
                detail = error.Message;
                transfers.Log($"{item.Name} failed. {detail}");
                if (operation is null)
                    SetFeedback(detail, error: true);
            }

            bool saved = ChangeQueue(() => archiveQueue.Finish(item.LocalName, outcome, detail));
            if (request is not null && options.RemoveOriginals)
                await RefreshArchiveSource(request);
            if (saved && outcome == ArchiveQueueState.Finished)
                await RefreshAfterArchive(item);
        }
        finally
        {
            archiveOperation = null;
            packageSurfPath = null;
            FinishWork();
        }
    }

    private async Task RefreshArchiveSource(ArchiveRequest request)
    {
        if (closeWhenIdle || localBrowsing != 0 || !windows.HasFolder)
            return;
        string source = request.Plan.Source;
        string visible = windows.PathShown;
        if (!visible.Equals(source, StringComparison.OrdinalIgnoreCase) &&
            !request.Items.Any(item => item.IsDirectory && PackageFiles.IsWithin(visible, Path.Combine(source, item.Name))))
            return;
        if (operation is not null)
        {
            pendingSourceRefresh = request;
            return;
        }

        int refresh = localListingVersion + 1;
        try
        {
            try { await NavigateLocal(visible, null); }
            catch (DirectoryNotFoundException) when (visible != source && localListingVersion == refresh &&
                operation is null && !closeWhenIdle)
            {
                await NavigateLocal(source, null);
            }
        }
        catch (Exception error)
        {
            transfers.Log($"Could not refresh the local source folder. {error.Message}");
        }
    }

    private async Task RefreshAfterArchive(ArchiveQueueItem item)
    {
        if (operation is not null || editingSurf || closeWhenIdle || archiveQueue.Items.Any(queued => queued.State == ArchiveQueueState.Queued))
            return;
        if (surfBrowsing == 0 && surf.PathShown == item.Parent)
            await Browse(local: false, () => NavigateRemote(item.Parent, null));
        if (!closeWhenIdle)
            SetFeedback($"{item.Name} was uploaded and verified.");
    }

    private async Task ResumeArchive(ArchiveQueueItem? selected = null)
    {
        if (archive is null || operation is not null || archiveOperation is not null || editingSurf || closeWhenIdle)
            return;
        await Run(async token =>
        {
            string? folder = selected is null ? PickRecoveryFolder() : selected.RecoveryPath;
            if (folder is null)
                return;

            PackageManifest manifest;
            try { manifest = await Task.Run(() => ArchivePackage.ReadManifest(folder), token); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new IOException($"No verified package.json was found in {folder}. Packing or verification may have been interrupted.", error);
            }
            if (manifest.ArchiveRoot != archive.Root)
                throw new IOException("This package was prepared for a different SURF account.");
            string remote = ArchiveSession.Child(manifest.Destination, manifest.RemoteName);
            if (selected is not null && (manifest.Name != selected.LocalName || remote != selected.RemotePath))
                throw new IOException("The local recovery package does not match this queue item.");
            var item = selected ?? archiveQueue.Items.FirstOrDefault(job => job.LocalName == manifest.Name &&
                job.RemotePath == remote && job.State is ArchiveQueueState.Interrupted or ArchiveQueueState.Failed or ArchiveQueueState.Cancelled);

            if (MessageBox.Show(this,
                $"Local recovery package:\n{folder}\n\nSURF destination:\n{remote}\n\nThe app uploads missing parts and checks every SURF checksum. It then removes the local package parts. Your original files stay in place.",
                "Resume archive upload", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            token.ThrowIfCancellationRequested();
            if (item is not null)
                archiveQueue.StartRecovery(item, folder);
            packageSurfPath = remote;
            UpdateControls();
            try
            {
                await RunPackage(manifest.RemoteName, folder, remote, (target, progress) =>
                    ArchivePackage.Resume(folder, target, progress, token, manifest), token, item?.LocalName);
            }
            catch (Exception error)
            {
                if (item is not null)
                {
                    var state = error is OperationCanceledException && token.IsCancellationRequested
                        ? closeWhenIdle ? ArchiveQueueState.Interrupted : ArchiveQueueState.Cancelled
                        : ArchiveQueueState.Failed;
                    ChangeQueue(() => archiveQueue.Finish(item.LocalName, state, error.Message));
                }
                throw;
            }
            finally { packageSurfPath = null; }
            if (item is not null)
                ChangeQueue(() => archiveQueue.Finish(item.LocalName, ArchiveQueueState.Finished));
            if (!closeWhenIdle && !editingSurf && surfBrowsing == 0 && surf.PathShown == manifest.Destination)
                await NavigateRemote(manifest.Destination, null);
            SetFeedback("Archive upload verified. Remaining local archive parts removed.");
        }, transfer: true);
    }

    private string? PickRecoveryFolder()
    {
        if (!PackageDialog.ExplainResume(this))
            return null;
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the local SURF package folder containing package.json.",
            UseDescriptionForTitle = true, ShowNewFolderButton = false, SelectedPath = windows.PathShown
        };
        return picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null;
    }

    private async Task RunPackage(string name, string recovery, string destination,
        Func<IPackageDestination, Action<PackageMeter>, Task> run, CancellationToken token, string? queueId)
    {
        var stages = new Dictionary<PackageStep, TransferJob>();
        var completed = new HashSet<PackageStep>();
        var gate = new object();
        if (operation is null || transferring)
            SetFeedback("Preparing archive destination…");
        try
        {
            await Task.Run(async () =>
            {
                using var target = archive!.PackageTarget();
                target.DestinationCreated += parent => BeginInvoke(() => _ = RefreshCreatedDestination(parent));
                await run(target, Report);
            });
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            string message = Directory.Exists(recovery) ? $"Local package folder kept: {recovery}" : "No package folder was created.";
            foreach (var stage in stages.Values)
            {
                if (!cancelled && !stage.Notes.Any(note => note.EndsWith(error.Message, StringComparison.Ordinal)))
                    transfers.Log(error.Message, stage);
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
                    var (caption, working, detail, showRate) = DescribeStep(meter.Step);
                    job = new TransferJob(name, caption, recovery, destination, meter.Total, meter.CountItems,
                        totalKnown: meter.TotalKnown, working: working, showRate: showRate && !meter.CountItems);
                    stages.Add(meter.Step, job);
                    Invoke(() =>
                    {
                        transfers.Add(job);
                        transfers.Log(detail, job);
                    });
                }
                if (meter.TotalOnly)
                    job.SetTotal(meter.Total);
                else
                    job.Report(new(name, meter.Done, meter.Total, meter.TotalKnown), meter.Phase);
                if (meter.Message is { } updateMessage)
                    Invoke(() => transfers.Log(updateMessage, job));
                if (meter.Finished)
                {
                    Invoke(() =>
                    {
                        transfers.Finish(job, meter.Skipped ? TransferState.Skipped : TransferState.Completed);
                        if (queueId is not null && completed.Add(meter.Step))
                            ChangeQueue(() => archiveQueue.CompleteSteps(queueId, completed.Count));
                    });
                    stages.Remove(meter.Step);
                }
            }
        }
    }

    private async Task RefreshCreatedDestination(string parent)
    {
        if (closeWhenIdle || editingSurf || surfBrowsing != 0 || (operation is not null && !transferring) || surf.PathShown != parent)
            return;
        try { await NavigateRemote(parent, null); }
        catch (Exception error)
        {
            if (!closeWhenIdle)
                transfers.Log($"Could not refresh {parent}. {error.Message}");
        }
    }

    private static (string Caption, string Working, string Detail, bool ShowRate) DescribeStep(PackageStep step) => step switch
    {
        PackageStep.Archive => ("Compress source", "Compressing", "Compresses large files with up to eight workers.", true),
        PackageStep.Verify => ("Check local archive", "Checking", "Checks every archive part. For new archives, decodes large files in parallel and checks the SHA-256 of every complete file against its source.", true),
        PackageStep.Index => ("Write archive metadata", "Writing", "Items indexed for the contents files. Also writes retention.json.", false),
        PackageStep.RemoveOriginals => ("Remove originals", "Removing", "Originals are removed after all SURF SHA-256 checks pass.", false),
        PackageStep.Upload => ("Upload to SURF", "Uploading", "Completed parts upload during packing. The final size includes compressed parts and metadata.", true),
        PackageStep.RemoteSha => ("Check SURF checksums", "Checking", "Package bytes covered by matching SHA-256 checksums. Updates after each file.", false),
        _ => ("Clean up local package", "Cleaning up", "Local package files checked for cleanup.", false)
    };

    private async Task ConfirmRemoval(bool local)
    {
        BrowserPane pane = local ? windows : surf;
        var items = pane.SelectedItems;
        string directory = pane.PathShown;
        if (items.Count == 0 || (!local && (!CanEditSurf || items.Any(item => !CanRemoveSurfItem(directory, item)))) ||
            !RemovalDialog.Confirm(this, local, directory, items))
            return;

        if (local)
            await Run(_ => RemoveSelected(null));
        else
            await RunSurfEdit(RemoveSelected);

        async Task RemoveSelected(ArchiveSession? session)
        {
            if (session is not null && items.Any(item => !CanRemoveSurfItem(directory, item)))
                throw new IOException("One or more selected items are in use by a SURF transfer.");
            int removed = 0;
            string? failure = null;
            foreach (var item in items)
            {
                SetFeedback($"Removing {item.Name}…");
                try
                {
                    if (session is null)
                        await Task.Run(() => LocalFiles.Remove(directory, item));
                    else
                        await Task.Run(() => session.Remove(directory, item));
                    removed++;
                    transfers.Log($"Removed from {(local ? "Local / NAS" : "SURF")}: {item.Name}");
                }
                catch (Exception error)
                {
                    failure ??= error.Message;
                    transfers.Log($"Removal failed or incomplete for {item.Name}: {error.Message}");
                }
            }
            if (session is null)
                await NavigateLocal(directory, null);
            else if (!closeWhenIdle && surfBrowsing == 0 && surf.PathShown == directory)
                await NavigateRemote(directory, null);
            int failed = items.Count - removed;
            string summary = $"Removed {removed:N0} of {items.Count:N0} selected items.";
            SetFeedback(failure is null ? summary : $"{summary} {failure}", error: failed > 0);
        }
    }

    private async Task MakeFolder()
    {
        if (!CanEditSurf)
            return;
        string directory = surf.PathShown;
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
        var create = DarkTheme.Button("Create", ActionIcon.FolderPlus, primary: true);
        var cancel = DarkTheme.Button("Cancel", ActionIcon.Cancel);
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

        await RunSurfEdit(async session =>
        {
            string path = ArchiveSession.Child(directory, name.Text.Trim());
            if (IsSurfPathInUse(path))
                throw new IOException("This folder path is in use by a SURF transfer. Choose a different name or location.");
            await Task.Run(() => session.MakeDirectory(path));
            if (!closeWhenIdle && surfBrowsing == 0 && surf.PathShown == directory)
                await NavigateRemote(directory, null);
            SetFeedback("Folder created.");
        });
    }

    private bool CanEditSurf => !closeWhenIdle && !editingSurf && archive is not null &&
        surf.HasFolder && (operation is null || transferring);

    private bool CanRemoveSurfItem(string directory, ArchiveEntry item) =>
        !((copySurfPaths.Length > 0 || packageSurfPath is not null) &&
            item.Name.StartsWith(".surfarchive-upload-", StringComparison.Ordinal)) &&
        !IsSurfPathInUse(ArchiveSession.Child(directory, item.Name));

    private bool IsSurfPathInUse(string path) =>
        (packageSurfPath is { } packagePath && PathsOverlap(path, packagePath)) ||
        copySurfPaths.Any(active => PathsOverlap(path, active)) || restores.Keys.Any(active => PathsOverlap(path, active));

    private static bool IsAtOrBelow(string path, string parent) =>
        path.Equals(parent, StringComparison.Ordinal) || path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static bool PathsOverlap(string path, string active) => IsAtOrBelow(path, active) || IsAtOrBelow(active, path);

    private async Task RunSurfEdit(Func<ArchiveSession, Task> edit)
    {
        if (!CanEditSurf || archive is not { } session)
            return;
        editingSurf = true;
        UpdateControls();
        try { await edit(session); }
        catch (Exception error)
        {
            transfers.Log(error.Message);
            SetFeedback(error.Message, error: true);
        }
        finally
        {
            editingSurf = false;
            FinishWork();
        }
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
            if (feedbackMessage == "Loading…")
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
            FinishWork();
        }
    }

    private RestoreActivity? SelectedRestore => transfers.SelectedJob is { Tape: true } job
        ? restores.Values.FirstOrDefault(item => item.Job == job && !item.Cancellation.IsCancellationRequested)
        : null;

    private void CancelSelectedActivity()
    {
        if (SelectedRestore is { } activity)
        {
            activity.Cancellation.Cancel();
            UpdateControls();
            return;
        }
        CancelTransfer();
    }

    private void CancelTransfer()
    {
        var active = archiveOperation ?? (transferring ? operation : null);
        if (active is null)
            return;
        active.Cancel();
        UpdateControls();
        SetFeedback("Cancelling activity…");
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        closeWhenIdle = true;
        StopRestores();
        if (operation is null && archiveOperation is null && !editingSurf)
            return;
        e.Cancel = true;
        operation?.Cancel();
        archiveOperation?.Cancel();
        UpdateControls();
    }

    private void FinishWork()
    {
        UpdateControls();
        if (closeWhenIdle)
        {
            if (operation is null && archiveOperation is null && !editingSurf)
                BeginInvoke(Close);
            return;
        }
        if (operation is null && pendingSourceRefresh is { } source)
        {
            pendingSourceRefresh = null;
            _ = RefreshArchiveSource(source);
        }
        TryStartNextArchive();
    }

    private void UpdateControls()
    {
        bool idle = operation is null && !editingSurf && !closeWhenIdle;
        bool transferIdle = idle && archiveOperation is null;
        bool online = archive is not null;
        signIn.Text = online ? "Sign out" : "Sign in";
        DarkTheme.SetIcon(signIn, online ? ActionIcon.SignOut : ActionIcon.SignIn);
        signIn.Enabled = transferIdle;
        connection.Text = online ? "●  SFTP connected  ·  " + ArchiveSession.Host : "Disconnected";
        connection.ForeColor = online ? DarkTheme.Mint : DarkTheme.Muted;
        windows.SetAvailable(CanBrowse, windows.HasFolder);
        surf.SetAvailable(CanBrowse && online, online && surf.HasFolder);
        var localSelection = windows.SelectedItems;
        upload.Enabled = transferIdle && online && surf.HasFolder && LocalFiles.CanUploadDirectly(localSelection);
        var selected = surf.SelectedItems;
        download.Enabled = transferIdle && online && windows.HasFolder && selected.Count > 0 &&
            selected.All(item => item.IsDirectory ? item.HasSubfolders != true : ArchiveStatus.IsOnDisk(item.Status));
        restore.Enabled = idle && online && selected.Any(item =>
            (item.IsDirectory ? item.HasSubfolders != true && !ArchiveStatus.IsOnDisk(item.Status) : ArchiveStatus.CanRestore(item.Status))
            && !restores.ContainsKey(ArchiveSession.Child(surf.PathShown, item.Name)));
        package.Enabled = idle && online && archiveQueue.IsAvailable && surf.HasFolder && ArchivePackage.CanArchive(localSelection);
        queuePanel.SetRecoveryAvailable(transferIdle && online);
        removeWindows.Enabled = transferIdle && windows.SelectedItems.Count > 0;
        removeSurf.Enabled = CanEditSurf && selected.Count > 0 && selected.All(item => CanRemoveSurfItem(surf.PathShown, item));
        newFolder.Enabled = CanEditSurf;
        var selectedRestore = SelectedRestore;
        string cancelCaption = selectedRestore is null ? "Cancel activity"
            : selectedRestore.Polling.OpenWhenReady ? "Cancel preview" : "Stop monitoring";
        transfers.SetCancel(cancelCaption,
            !closeWhenIdle && (selectedRestore is not null || archiveOperation is { IsCancellationRequested: false }
                || (transferring && operation is { IsCancellationRequested: false })));
    }

    private void SetFeedback(string text, bool error = false)
    {
        feedbackMessage = text;
        feedbackIsError = error;
        if (archiveScans.Count == 0)
            RefreshScanStatus();
    }

    private sealed record RestoreActivity(TransferJob Job, RestorePolling Polling, CancellationTokenSource Cancellation);

    private sealed record DirectCopy(bool ToSurf, string LocalDirectory, string SurfDirectory, IReadOnlyList<ArchiveEntry> Items);

    private sealed record ActiveArchiveScan(CancellationTokenSource Cancellation, ArchiveScanProgress Progress)
    {
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
    }
}
