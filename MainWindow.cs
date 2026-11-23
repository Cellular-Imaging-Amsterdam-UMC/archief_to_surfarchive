namespace SurfArchive;

internal sealed class MainWindow : Form
{
    private readonly BrowserPane windows = new("Local / NAS", local: true);
    private readonly BrowserPane surf = new("SURF Data Archive", local: false);
    private readonly TransferLog transfers;
    private readonly SessionLog? sessionLog;
    private readonly ArchiveQueue archiveQueue = new();
    private readonly ArchiveQueuePanel queuePanel = new();
    private readonly RowStyle actionHeight = new(SizeType.Absolute, 35);
    private readonly RowStyle queueHeight = new(SizeType.Absolute, ArchiveQueuePanel.EmptyHeight);
    private readonly RowStyle activityHeight = new(SizeType.Absolute, TransferLog.DisplayHeight);
    private readonly RowStyle statusHeight = new(SizeType.Absolute, 32);
    private readonly Button upload = DarkTheme.Button("Upload to SURF", ActionIcon.Upload, primary: true);
    private readonly Button download = DarkTheme.Button("Download", ActionIcon.Download, primary: true);
    private readonly Button restore = DarkTheme.Button("Restore from tape", ActionIcon.Tape);
    private readonly Button package = DarkTheme.Button("Archive to SURF…", ActionIcon.Archive, primary: true);
    private readonly Button removeWindows = DarkTheme.Button("Remove…", ActionIcon.Trash);
    private readonly Button removeSurf = DarkTheme.Button("Remove…", ActionIcon.Trash);
    private readonly Button newFolder = DarkTheme.Button("New folder", ActionIcon.FolderPlus);
    private readonly Button logs = DarkTheme.Button("Logs", ActionIcon.Browse);
    private readonly Label feedback = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 200 };
    private ActiveArchiveScan? activeArchiveScan;
    private LogWindow? logWindow;
    private Dictionary<string, ArchiveQueueItem> loggedQueue = new(StringComparer.Ordinal);
    private ArchiveSession? archive;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? archiveOperation;
    private CancellationTokenSource? statusLookup;
    private readonly Dictionary<string, RestoreActivity> restores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim retentionEditor = new(1);
    private string[] copySurfPaths = [];
    private string[] downloadLocalPaths = [];
    private string? packageSurfPath;
    private string? recoveryLocalPath;
    private ArchiveRequest? pendingSourceRefresh;
    private int localListingVersion;
    private int surfListingVersion;
    private int localBrowsing;
    private int surfBrowsing;
    private bool transferring;
    private bool editingSurf;
    private bool editingLocal;
    private bool panelResizePending;
    private bool closeWhenIdle;
    private string feedbackMessage = "";
    private bool feedbackIsError;

    public MainWindow(ArchiveSession? session, SessionLog? sessionLog = null, Screen? startupScreen = null)
    {
        archive = session;
        this.sessionLog = sessionLog;
        transfers = new(sessionLog);
        SuspendLayout();
        DarkTheme.Window(this);
        Text = session is null ? "SurfDataArchiveTransfer"
            : $"SurfDataArchiveTransfer · SFTP connected · {ArchiveSession.Host}";
        Rectangle workArea = (startupScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Bounds = workArea;
        BuildLayout();
        scanTimer.Tick += (_, _) => RefreshScanStatus();
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
        upload.Click += (_, _) => UploadDirect();
        download.Click += async (_, _) => await DownloadSelected();
        restore.Click += (_, _) => StartRestores();
        package.Click += (_, _) => ArchiveSelected();
        removeWindows.Click += async (_, _) => await ConfirmRemoval(local: true);
        removeSurf.Click += async (_, _) => await ConfirmRemoval(local: false);
        removeWindows.AccessibleName = "Remove selected Local / NAS items";
        removeSurf.AccessibleName = "Remove selected SURF items";
        newFolder.Click += async (_, _) => await MakeFolder();
        logs.Click += (_, _) => OpenLogs();
        transfers.CancelRequested += CancelSelectedActivity;
        transfers.SelectionChanged += UpdateControls;
        transfers.HorizontalScrollbarChanged += (_, _) => SchedulePanelResize();
        queuePanel.HorizontalScrollbarChanged += (_, _) => SchedulePanelResize();
        SizeChanged += (_, _) => SchedulePanelResize();
        Load += (_, _) =>
        {
            Bounds = StartupBounds(workArea);
            FitToScreen();
        };
        DpiChanged += (_, _) => BeginInvoke(FitToScreen);
        archiveQueue.Changed += () =>
        {
            LogQueueChanges();
            queuePanel.ShowItems(archiveQueue.Items);
            FitActivityPanels();
            UpdateControls();
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
        AutoScaleDimensions = DarkTheme.DesignDpi;
        ResumeLayout(performLayout: false);
    }

    private void SchedulePanelResize()
    {
        // Scrollbar visibility can change while the grid is laying out its rows.
        if (!IsHandleCreated || IsDisposed || panelResizePending)
            return;
        panelResizePending = true;
        BeginInvoke(() =>
        {
            panelResizePending = false;
            FitActivityPanels();
        });
    }

    private void FitActivityPanels()
    {
        if (IsDisposed || transfers.Parent is not TableLayoutPanel layout)
            return;
        var actions = (TableLayoutPanel)layout.GetControlFromPosition(0, 1)!;
        int toolbarHeight = actions.Controls.Cast<FlowLayoutPanel>()
            .Max(toolbar => toolbar.GetPreferredSize(new Size(Math.Max(1, toolbar.Width), int.MaxValue)).Height)
            + actions.Margin.Vertical;
        int footerHeight = Math.Max(feedback.PreferredHeight, logs.GetPreferredSize(Size.Empty).Height)
            + logs.Parent!.Margin.Vertical;
        float scale = DeviceDpi / 96f;
        int queued = Math.Min(archiveQueue.Items.Count, 4);
        int activityRows = 4;
        float queue = queuePanel.HeightFor(queued);
        float activity = transfers.HeightFor(activityRows);
        int browserSpace = (int)Math.Round(260 * scale);
        int usable = ClientSize.Height - browserSpace - queuePanel.Margin.Vertical - transfers.Margin.Vertical -
            toolbarHeight - footerHeight;
        while (queue + activity > usable && activityRows > 1)
            activity = transfers.HeightFor(--activityRows);
        while (queue + activity > usable && queued > 1)
            queue = queuePanel.HeightFor(--queued);
        if (queueHeight.Height == queue && activityHeight.Height == activity &&
            actionHeight.Height == toolbarHeight && statusHeight.Height == footerHeight)
            return;
        actionHeight.Height = toolbarHeight;
        queueHeight.Height = queue;
        activityHeight.Height = activity;
        statusHeight.Height = footerHeight;
        layout.PerformLayout();
    }

    private void FitToScreen()
    {
        Size available = StartupBounds(Screen.FromControl(this).WorkingArea).Size;
        float scale = DeviceDpi / 96f;
        MinimumSize = new Size(Math.Min((int)Math.Round(960 * scale), available.Width),
            Math.Min((int)Math.Round(600 * scale), available.Height));
        SchedulePanelResize();
    }

    private static Rectangle StartupBounds(Rectangle workArea) =>
        Rectangle.Inflate(workArea, -workArea.Width / 8, -workArea.Height / 8);

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(actionHeight);
        layout.RowStyles.Add(queueHeight);
        layout.RowStyles.Add(activityHeight);
        layout.RowStyles.Add(statusHeight);
        Controls.Add(layout);

        var panes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(10, 4, 10, 1)
        };
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new(SizeType.Percent, 50));
        panes.Controls.Add(windows, 0, 0);
        panes.Controls.Add(surf, 1, 0);
        layout.Controls.Add(panes, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(13, 5, 13, 1) };
        actions.ColumnStyles.Add(new(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new(SizeType.Percent, 50));
        actions.RowStyles.Add(new(SizeType.Percent, 100));
        actions.Controls.Add(DarkTheme.Toolbar(package, upload, removeWindows), 0, 0);
        actions.Controls.Add(DarkTheme.Toolbar(download, restore, newFolder, removeSurf), 1, 0);
        actions.SizeChanged += (_, _) => SchedulePanelResize();
        foreach (Control toolbar in actions.Controls)
            toolbar.Layout += (_, _) => SchedulePanelResize();
        layout.Controls.Add(actions, 0, 1);

        queuePanel.Margin = new Padding(13, 0, 13, 4);
        layout.Controls.Add(queuePanel, 0, 2);
        transfers.Margin = new Padding(13, 0, 13, 0);
        layout.Controls.Add(transfers, 0, 3);
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        feedback.UseMnemonic = false;
        feedback.TextAlign = ContentAlignment.MiddleLeft;
        feedback.Dock = DockStyle.Fill;
        feedback.Margin = Padding.Empty;
        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(14, 2, 13, 2)
        };
        statusBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        statusBar.ColumnStyles.Add(new(SizeType.AutoSize));
        statusBar.RowStyles.Add(new(SizeType.Percent, 100));
        logs.Margin = new Padding(8, 0, 0, 0);
        statusBar.Controls.Add(feedback, 0, 0);
        statusBar.Controls.Add(logs, 1, 0);
        statusBar.SizeChanged += (_, _) => SchedulePanelResize();
        logs.FontChanged += (_, _) => SchedulePanelResize();
        feedback.FontChanged += (_, _) => SchedulePanelResize();
        layout.Controls.Add(statusBar, 0, 4);
    }

    private void OpenLogs()
    {
        if (logWindow is { IsDisposed: false } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }
        logWindow = new LogWindow(sessionLog);
        logWindow.Show(this);
    }

    private void LogQueueChanges()
    {
        if (sessionLog is null)
            return;
        if (!archiveQueue.IsAvailable)
        {
            loggedQueue.Clear();
            return;
        }
        var next = archiveQueue.Items.ToDictionary(item => item.LocalName, StringComparer.Ordinal);
        foreach (var item in next.Values)
        {
            if (loggedQueue.TryGetValue(item.LocalName, out var previous) && previous.State == item.State)
                continue;
            sessionLog.Write($"Queue · {item.Name} · {item.State} · {item.Source} → {item.RemotePath}" +
                (item.Detail is null ? "" : $"\n{item.Detail}"));
        }
        foreach (var item in loggedQueue.Values.Where(item => !next.ContainsKey(item.LocalName)))
            sessionLog.Write($"Queue · {item.Name} · Removed from queue");
        loggedQueue = next;
    }

    private async Task<ArchiveRequest> ScanArchive(ArchiveQueueItem item, CancellationToken token)
    {
        var job = new TransferJob(item.Name, "Scan source", item.Source, item.RemotePath, 0,
            countItems: true, totalKnown: false, working: "Scanning", showRate: false);
        var active = new ActiveArchiveScan(job, new("Scanning selection"), token);
        activeArchiveScan = active;
        transfers.Add(job);
        RefreshScanStatus();
        try
        {
            var request = await Task.Run(() => item.Prepare(token, active.Progress), token);
            token.ThrowIfCancellationRequested();
            long count = request.Plan.Entries.Count;
            job.Report(new(item.Name, count, count));
            string staging = request.Plan.UsesFallback ? "Fallback staging folder" : "Staging folder";
            transfers.Log($"{staging} {request.Plan.StagingParent} needs approximately {TransferJob.FormatBytes(request.Plan.Required)}.", job);
            transfers.Finish(job, TransferState.Completed);
            return request;
        }
        catch (Exception error)
        {
            RefreshScanStatus();
            bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            if (!cancelled)
                transfers.Log(error.Message, job);
            transfers.Finish(job, cancelled ? TransferState.Cancelled : TransferState.Failed, cancelled ? null : error.Message);
            throw;
        }
        finally
        {
            activeArchiveScan = null;
            RefreshScanStatus();
        }
    }

    private void RefreshScanStatus()
    {
        var active = activeArchiveScan;
        scanTimer.Enabled = active is not null;
        if (active is null)
        {
            feedback.Text = feedbackMessage;
            feedback.ForeColor = feedbackIsError ? DarkTheme.Error : DarkTheme.Muted;
            return;
        }
        var (phase, files, folders, bytes) = active.Progress.Read();
        TimeSpan elapsed = active.Job.Snapshot().Elapsed;
        active.Job.Report(new(active.Job.Name, (long)files + folders, 0, TotalKnown: false));
        string name = active.Job.Name is { Length: > 35 } longName ? longName[..34] + "…" : active.Job.Name;
        string label = active.Token.IsCancellationRequested ? "Cancelling scan…" : phase;
        feedback.ForeColor = DarkTheme.Mint;
        feedback.Text = $"{label} ({name})  ·  {(long)files + folders:N0} items ({files:N0} files, {folders:N0} folders)" +
            $"  ·  {TransferJob.FormatBytes(bytes)} of file data found  ·  {TransferJob.FormatTime(elapsed)}";
    }

    private async Task InitialLoad(CancellationToken token)
    {
        _ = Task.Run(FilePreview.PruneTemporaryFolders, token);
        var local = LoadLocalLocation();
        if (archive is null)
        {
            await local;
            return;
        }
        await Task.WhenAll(local, LoadSurfLocation(archive.Root), LoadArchiveQueue(archive.Root, token));
    }

    private async Task LoadLocalLocation()
    {
        if (await TryOpenLastFolder(local: true) || closeWhenIdle)
            return;
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

    private async Task LoadSurfLocation(string root)
    {
        if (!await TryOpenLastFolder(local: false) && !closeWhenIdle)
            await NavigateRemote(root, "My archive");
    }

    private async Task<bool> TryOpenLastFolder(bool local)
    {
        var pane = local ? windows : surf;
        if (pane.LastVisited is not { } folder)
            return false;
        try
        {
            await OpenRecent(folder, local);
            return pane.HasFolder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            Renci.SshNet.Common.SshException)
        {
            transfers.Log($"Could not reopen {folder.Path}. {error.Message}");
            return false;
        }
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
                sessionLog?.Write($"Local / NAS · Opened {full}");
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
            sessionLog?.Write($"SURF · Opened {path}");
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
            sessionLog?.Write($"{(local ? "Local / NAS" : "SURF")} · Opened recent folder {listing.Path}");
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

    private void UploadDirect()
    {
        var items = windows.SelectedItems;
        if (!CanQueueArchive || !LocalFiles.CanUploadDirectly(items))
            return;
        string source = windows.PathShown;
        string destination = surf.PathShown;
        if (MessageBox.Show(this,
            "Archive to SURF is recommended. It bundles and verifies your data and creates a contents list.\n\n" +
            $"Add {items.Count:N0} file(s) to the queue for direct upload to {destination}?",
            "Direct upload to SURF", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.OK || !CanQueueArchive)
            return;
        if (!ChangeQueue(() => archiveQueue.Add(items.Select(item => ArchiveQueueItem.FromUpload(source, destination, item)).ToArray())))
            return;
        SetFeedback($"{items.Count:N0} file(s) added to the upload queue.");
        TryStartNextArchive();
    }

    private Task DownloadSelected()
    {
        var items = surf.SelectedItems;
        if (archive is not { } session || items.Count == 0 || HasActiveWork || closeWhenIdle)
            return Task.CompletedTask;
        string source = surf.PathShown;
        string destination = windows.PathShown;
        return Run(async token =>
        {
            copySurfPaths = items.Select(item => ArchiveSession.Child(source, item.Name)).ToArray();
            downloadLocalPaths = items.Where(item => item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                .Select(item => Path.Combine(destination, item.Name)).ToArray();
            UpdateControls();
            try { await DownloadFiles(session, source, destination, items, token); }
            finally
            {
                copySurfPaths = [];
                downloadLocalPaths = [];
                if (!closeWhenIdle && surfBrowsing == 0 && surf.PathShown == source)
                    _ = LoadArchiveStatuses(source, surf.SnapshotEntries());
            }
        }, transfer: true);
    }

    private async Task DownloadFiles(ArchiveSession session, string directory, string destination,
        IReadOnlyList<ArchiveEntry> items, CancellationToken token)
    {
        int failures = 0, waitingForTape = 0;
        foreach (var entry in items)
        {
            token.ThrowIfCancellationRequested();
            string source = ArchiveSession.Child(directory, entry.Name);
            string action = entry.IsDirectory ? "Download folder" : "← Download";
            var job = new TransferJob(entry.Name, action, source, destination, entry.IsDirectory ? 0 : entry.Size);
            transfers.Add(job);

            try
            {
                var progress = new Callback<TransferProgress>(job.Report);
                if (entry.IsDirectory)
                {
                    job.SetPhase(TransferState.CheckingStatus);
                    var folder = await Task.Run(() => session.ScanFolder(source, entry.Name, token));
                    folder.Destination(destination);
                    int offline = await Task.Run(() => session.CountFolderFilesToRestore(folder, token));
                    if (offline > 0)
                    {
                        if (surf.PathShown == directory)
                            surf.ApplyFolderStatus(new(entry.Name, ArchiveState.OFL, false));
                        waitingForTape++;
                        transfers.Log($"{offline:N0} of {folder.Files.Count:N0} files need a disk copy. Download the folder once it is ready.", job);
                        if (!restores.ContainsKey(source) && MessageBox.Show(this,
                            $"Restore {offline:N0} file(s) in '{entry.Name}' to disk on SURF?\n\n" +
                            "When Activity shows Ready to download, select the folder and click Download again.",
                            "Restore folder from tape", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                            QueueRestore(directory, entry, openWhenReady: false, folder);
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

        if (!closeWhenIdle && localBrowsing == 0 &&
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
                    transfers.Log($"Checking the current {RetentionMetadata.FileName} before opening {PackageContents.HtmlName}.", job);
                    var (metadata, status) = await ReadCompanionRetention(local);
                    bool rendered = await Task.Run(() => ContentsPreview.AddRetention(localPath, metadata, status, token));
                    if (!rendered && !IsDisposed)
                        transfers.Log("This contents file format does not support the retention preview.", job);
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
                SetFeedback($"Checking {RetentionMetadata.FileName} before opening {PackageContents.HtmlName}…");
            try
            {
                bool exists = await Task.Run(() => session.HasRetentionForPreview(remotePath, token));
                token.ThrowIfCancellationRequested();
                if (!exists)
                {
                    const string missing = "This SURF folder has no " + RetentionMetadata.FileName + ".";
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
                transfers.Log($"Read the current {RetentionMetadata.FileName} from the same SURF folder.", job);
                return (metadata, $"Read from {RetentionMetadata.FileName} in this SURF folder when the preview was opened.");
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                if (!IsDisposed)
                    transfers.Log($"Could not read {RetentionMetadata.FileName}. {error.Message}", job);
                return (null, $"Could not read the current {RetentionMetadata.FileName}. See Activity in SurfDataArchiveTransfer.");
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

    private bool CanQueueArchive => CanBrowse && !editingSurf && !editingLocal &&
        archive is not null && archiveQueue.IsAvailable && surf.HasFolder;

    private void ArchiveSelected()
    {
        if (!CanQueueArchive)
            return;
        var selection = windows.SelectedItems;
        if (!ArchivePackage.CanArchive(selection))
            return;
        string source = windows.PathShown;
        string destination = surf.PathShown;
        var existing = surf.SnapshotEntries().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        try
        {
            string? filesFolderName = null;
            if (!selection[0].IsDirectory)
            {
                filesFolderName = PackageDialog.AskFolderName(this);
                if (filesFolderName is null)
                    return;
            }
            var requests = ArchivePackage.DescribeSelection(source, destination, selection, filesFolderName);
            archiveQueue.CheckDestinations(requests.Select(request => request.RemotePath));
            foreach (var request in requests)
                if (existing.Contains(request.Name))
                    throw new IOException($"The SURF destination already exists at {request.RemotePath}. Choose a different parent folder or name.");
            var options = PackageDialog.Confirm(this, requests);
            if (options is null || closeWhenIdle)
                return;
            archiveQueue.Add(requests.Select(request => ArchiveQueueItem.FromSelection(request, options)).ToArray());
            SetFeedback(requests.Count == 1 ? "Archive added to the queue." : $"{requests.Count:N0} archives added to the queue.");
            TryStartNextArchive();
        }
        catch (Exception error)
        {
            transfers.Log(error.Message);
            SetFeedback(error.Message, error: true);
        }
    }

    private void TryStartNextArchive()
    {
        if (HasActiveWork || archive is null || closeWhenIdle)
            return;
        ArchiveQueueItem? item = null;
        if (ChangeQueue(() => item = archiveQueue.StartNext()) && item is not null)
            _ = RunQueuedItem(item);
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
            string message = $"Could not save the queue. {error.Message}";
            transfers.Log(message);
            SetFeedback(message, error: true);
            return false;
        }
    }

    private async Task RunQueuedItem(ArchiveQueueItem item)
    {
        using var cancellation = new CancellationTokenSource();
        archiveOperation = cancellation;
        packageSurfPath = item.RemotePath;
        var token = cancellation.Token;
        UpdateControls();
        ArchiveQueueState outcome = ArchiveQueueState.Failed;
        string? detail = null;
        ArchiveRequest? request = null;
        try
        {
            try
            {
                if (item.DirectUpload)
                    await UploadQueuedFile(item, token);
                else
                {
                    var options = item.Options!;
                    options = options with
                    {
                        Retention = options.Retention with { ArchivedOn = DateOnly.FromDateTime(DateTime.Today) }
                    };
                    request = await ScanArchive(item, token);
                    archiveQueue.SetRecoveryPath(item, request.LocalPath);
                    ChangeQueue(() => archiveQueue.CompleteSteps(item.LocalName, ArchiveQueue.ScanSteps));
                    await RunPackage(request.Name, request.LocalPath, request.RemotePath, (target, progress) =>
                        ArchivePackage.Create(request.Plan, request.LocalName, request.Name, request.Parent,
                            target, options, progress, token, localReference: item.LocalReferencePath),
                        token, item.LocalName, startingSteps: ArchiveQueue.ScanSteps);
                }
                outcome = ArchiveQueueState.Finished;
                transfers.Log($"{item.Name} was uploaded and verified.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                outcome = closeWhenIdle ? ArchiveQueueState.Interrupted : ArchiveQueueState.Cancelled;
                detail = closeWhenIdle ? "The app closed while this job was running." : "Job cancelled.";
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
            if (request is not null && (item.LocalReferencePath is not null || item.Options?.RemoveOriginals == true))
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

    private async Task UploadQueuedFile(ArchiveQueueItem item, CancellationToken token)
    {
        var session = archive!;
        var file = item.Selection[0];
        string source = Path.Combine(item.Source, file.Name);
        var job = new TransferJob(file.Name, "Upload →", source, item.RemotePath, file.Size);
        transfers.Add(job);
        queuePanel.TrackDirectUpload(job);
        try
        {
            var progress = new Callback<TransferProgress>(job.Report);
            await Task.Run(() => session.Upload(source, item.Parent, progress, token, LocalFiles.MinimumDirectUpload, file));
            transfers.Log("Upload complete. File size verified.", job);
            transfers.Finish(job, TransferState.Completed);
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            transfers.Log(cancelled ? "Upload cancelled." : error.Message, job);
            transfers.Finish(job, cancelled ? TransferState.Cancelled : TransferState.Failed);
            throw;
        }
        finally { queuePanel.TrackDirectUpload(null); }
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
        if (operation is not null || editingSurf || closeWhenIdle ||
            (!item.DirectUpload && archiveQueue.Items.Any(queued => queued.State == ArchiveQueueState.Queued)))
            return;
        if (surfBrowsing == 0 && surf.PathShown == item.Parent)
            await Browse(local: false, () => NavigateRemote(item.Parent, null));
        if (!closeWhenIdle)
            SetFeedback($"{item.Name} was uploaded and verified.");
    }

    private async Task ResumeArchive(ArchiveQueueItem? selected = null)
    {
        if (archive is null || HasActiveWork || closeWhenIdle)
            return;
        await Run(async token =>
        {
            string? folder = selected is null ? PickRecoveryFolder() : selected.RecoveryPath;
            if (folder is null)
                return;

            recoveryLocalPath = folder;
            UpdateControls();
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
            string? reference = PackageContents.ReferenceParts(manifest).Length == 3 ? item?.LocalReferencePath : null;
            string referenceNote = reference is null ? "" : $"\n\nThe app also writes or verifies the local reference folder at {reference}.";

            if (MessageBox.Show(this,
                $"Local recovery package:\n{folder}\n\nSURF destination:\n{remote}\n\nThe app uploads missing parts and checks every SURF checksum. It then removes the local package parts. Your original files stay in place.{referenceNote}",
                "Resume archive upload", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            token.ThrowIfCancellationRequested();
            if (item is not null)
                archiveQueue.StartRecovery(item, folder, restoreReference: reference is not null);
            packageSurfPath = remote;
            UpdateControls();
            try
            {
                await RunPackage(manifest.RemoteName, folder, remote, (target, progress) =>
                    ArchivePackage.Resume(folder, target, progress, token, manifest, reference), token, item?.LocalName);
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
            if (!closeWhenIdle && localBrowsing == 0 && reference is not null && Path.GetDirectoryName(reference) is { } parent &&
                windows.PathShown.Equals(parent, StringComparison.OrdinalIgnoreCase))
                await Browse(local: true, () => NavigateLocal(parent, null));
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
        Func<IPackageDestination, Action<PackageMeter>, Task> run, CancellationToken token, string? queueId,
        int startingSteps = 0)
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
            if (stages.Count == 0 && !cancelled)
            {
                var failed = new TransferJob(name, "Archive job", recovery, destination, 0, totalKnown: false);
                transfers.Add(failed);
                transfers.Finish(failed, TransferState.Failed, $"{error.Message}\n\n{message}");
            }
            foreach (var stage in stages.Values)
            {
                if (!cancelled && !stage.Notes.Any(note => note.EndsWith(error.Message, StringComparison.Ordinal)))
                    transfers.Log(error.Message, stage);
                transfers.Log(message, stage);
                transfers.Finish(stage, cancelled ? TransferState.Cancelled : TransferState.Failed,
                    cancelled ? null : $"{error.Message}\n\n{message}");
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
                            ChangeQueue(() => archiveQueue.CompleteSteps(queueId, startingSteps + completed.Count));
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
        PackageStep.Index => ("Write archive metadata", "Writing", "Items indexed for the contents files and retention metadata.", false),
        PackageStep.LocalReference => ("Save local reference", "Saving", "Copies the verified metadata to the __sda_ folder next to the source.", false),
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
        if (items.Count == 0 ||
            (local ? !CanEditLocal || !CanRemoveLocalItems(directory, items, ProtectedLocalPaths())
                : !CanEditSurf || items.Any(item => !CanRemoveSurfItem(directory, item))) ||
            !RemovalDialog.Confirm(this, local, directory, items))
            return;

        if (local)
            await RunLocalEdit(async () =>
            {
                var protectedPaths = ProtectedLocalPaths();
                if (!CanRemoveLocalItems(directory, items, protectedPaths) ||
                    !await Task.Run(() => LocalFiles.CanRemoveResolvedPaths(directory, items, protectedPaths)))
                    throw new IOException("A selected item is used by a running or queued job. Remove it from the queue or wait for the job to finish.");
                await RemoveSelected(null);
            });
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
                    string path = session is null ? Path.Combine(directory, item.Name) : ArchiveSession.Child(directory, item.Name);
                    transfers.Log($"Removal failed or incomplete for {path}. {error.Message}");
                    if (session is null && item.IsDirectory && error is IOException io && LocalFiles.IsDirectoryNotEmpty(io))
                        transfers.Log(await Task.Run(() => LocalFiles.DescribeNonEmptyDirectory(path)));
                }
            }
            if (session is null && !closeWhenIdle && localBrowsing == 0 &&
                windows.PathShown.Equals(directory, StringComparison.OrdinalIgnoreCase))
                await NavigateLocal(directory, null);
            else if (session is not null && !closeWhenIdle && surfBrowsing == 0 && surf.PathShown == directory)
                await NavigateRemote(directory, null);
            int failed = items.Count - removed;
            string summary = $"Removed {removed:N0} of {items.Count:N0} selected items.";
            SetFeedback(failure is null ? summary : $"{summary} {failure}", error: failed > 0);
        }
    }

    private bool CanEditLocal => CanBrowse && windows.HasFolder && !editingLocal;

    private string[] ProtectedLocalPaths()
    {
        List<string> paths = [.. downloadLocalPaths];
        if (recoveryLocalPath is { } recovery)
            paths.Add(recovery);
        foreach (var job in archiveQueue.Items)
        {
            if (job.State is not (ArchiveQueueState.Queued or ArchiveQueueState.Paused or ArchiveQueueState.Running))
                continue;
            paths.AddRange(job.Selection.Select(item => Path.Combine(job.Source, item.Name)));
            if (job.LocalReferencePath is { } reference)
                paths.Add(reference);
            if (job.State != ArchiveQueueState.Running || job.DirectUpload)
                continue;
            if (job.RecoveryPath is { } staging)
                paths.Add(staging);
            else
            {
                paths.Add(Path.Combine(job.Options?.LocalStagingFolder ?? job.Source, job.LocalName));
                paths.Add(Path.Combine(LocalFiles.ActiveArchive, PackageFiles.FallbackFolderName, job.LocalName));
                paths.Add(Path.Combine(LocalFiles.ActiveArchive, "Personal Archive", PackageFiles.FallbackFolderName, job.LocalName));
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool CanRemoveLocalItems(string directory, IReadOnlyList<ArchiveEntry> items, string[] protectedPaths) =>
        items.All(item => !(downloadLocalPaths.Length > 0 && LocalFiles.IsDownloadStagingName(item.Name)) &&
            !protectedPaths.Any(path => LocalFiles.PathsOverlap(path, Path.Combine(directory, item.Name))));

    private async Task RunLocalEdit(Func<Task> edit)
    {
        if (!CanEditLocal)
            return;
        editingLocal = true;
        UpdateControls();
        try { await edit(); }
        catch (Exception error)
        {
            transfers.Log(error.Message);
            SetFeedback(error.Message, error: true);
        }
        finally
        {
            editingLocal = false;
            FinishWork();
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
            transfers.Log($"Created SURF folder {path}.");
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
            recoveryLocalPath = null;
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
        if (activeArchiveScan is not null)
            RefreshScanStatus();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        closeWhenIdle = true;
        StopRestores();
        if (!HasActiveWork)
            return;
        e.Cancel = true;
        operation?.Cancel();
        archiveOperation?.Cancel();
        UpdateControls();
    }

    private bool HasActiveWork => operation is not null || archiveOperation is not null || editingSurf || editingLocal;

    private void FinishWork()
    {
        UpdateControls();
        if (closeWhenIdle)
        {
            if (!HasActiveWork)
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
        bool transferIdle = idle && archiveOperation is null && !editingLocal;
        bool online = archive is not null;
        windows.SetAvailable(CanBrowse, windows.HasFolder);
        surf.SetAvailable(CanBrowse && online, online && surf.HasFolder);
        var localSelection = windows.SelectedItems;
        upload.Enabled = CanQueueArchive && LocalFiles.CanUploadDirectly(localSelection);
        var selected = surf.SelectedItems;
        download.Enabled = transferIdle && online && windows.HasFolder && selected.Count > 0 &&
            selected.All(item => item.IsDirectory ? item.HasSubfolders != true : ArchiveStatus.IsOnDisk(item.Status));
        restore.Enabled = idle && online && selected.Any(item =>
            (item.IsDirectory ? item.HasSubfolders != true && !ArchiveStatus.IsOnDisk(item.Status) : ArchiveStatus.CanRestore(item.Status))
            && !restores.ContainsKey(ArchiveSession.Child(surf.PathShown, item.Name)));
        package.Enabled = CanQueueArchive && ArchivePackage.CanArchive(localSelection);
        queuePanel.SetRecoveryAvailable(transferIdle && online);
        removeWindows.Enabled = CanEditLocal && localSelection.Count > 0 &&
            CanRemoveLocalItems(windows.PathShown, localSelection, ProtectedLocalPaths());
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
        if (text.Length > 0 && (text != feedbackMessage || error != feedbackIsError))
            sessionLog?.Write($"{(error ? "Error" : "Status")} · {text}");
        feedbackMessage = text;
        feedbackIsError = error;
        if (activeArchiveScan is null)
            RefreshScanStatus();
    }

    private sealed record RestoreActivity(TransferJob Job, RestorePolling Polling, CancellationTokenSource Cancellation);

    private sealed record ActiveArchiveScan(TransferJob Job, ArchiveScanProgress Progress, CancellationToken Token);
}
