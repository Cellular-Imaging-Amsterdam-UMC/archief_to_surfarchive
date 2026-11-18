namespace SurfArchive;

internal enum ArchiveState
{
    Unknown, REG, DUL, OFL, QUE, STG
}

internal static class ArchiveStatus
{
    public static bool IsOnDisk(ArchiveState? state) => state is ArchiveState.REG or ArchiveState.DUL;
    public static bool CanRestore(ArchiveState? state) => state is ArchiveState.OFL or ArchiveState.QUE or ArchiveState.STG;

    public static ArchiveState FolderState(IEnumerable<ArchiveState> states)
    {
        var found = states.ToHashSet();
        foreach (var state in new[] { ArchiveState.OFL, ArchiveState.STG, ArchiveState.QUE, ArchiveState.Unknown })
            if (found.Contains(state))
                return state;
        return ArchiveState.REG;
    }

    public static string Label(ArchiveState state) => state switch
    {
        ArchiveState.REG => "REG · Disk",
        ArchiveState.DUL => "DUL · Disk + tape",
        ArchiveState.OFL => "OFL · Tape only",
        ArchiveState.QUE => "QUE · Queued",
        ArchiveState.STG => "STG · Restoring",
        _ => "Unknown"
    };

    public static ArchiveState Parse(string value) => value.Trim() switch
    {
        "REG" => ArchiveState.REG,
        "DUL" => ArchiveState.DUL,
        "OFL" => ArchiveState.OFL,
        "QUE" => ArchiveState.QUE,
        "STG" => ArchiveState.STG,
        _ => ArchiveState.Unknown
    };

    public static Dictionary<string, ArchiveState> ParseRows(string output)
    {
        var states = new Dictionary<string, ArchiveState>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.LastIndexOf(',');
            if (separator > 0 && line[0] == '/')
                states[line[..separator]] = Parse(line[(separator + 1)..]);
        }
        return states;
    }
}

internal record RestoreUpdate(TransferState Phase, ArchiveState? State, string Message);

internal sealed class RestorePolling
{
    public static readonly TimeSpan ManualInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(15);
    private readonly TaskCompletionSource preview = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool OpenWhenReady => preview.Task.IsCompletedSuccessfully;
    public bool RequestPreview() => preview.TrySetResult();

    public async Task WaitForNextCheck(CancellationToken token)
    {
        if (OpenWhenReady)
        {
            await Task.Delay(PreviewInterval, token);
        }
        else
        {
            await Task.WhenAny(Task.Delay(ManualInterval, token), preview.Task);
            token.ThrowIfCancellationRequested();
        }
    }
}

internal static class TapeRestore
{
    public static async Task WaitForFolder(IReadOnlyList<string> files,
        Func<CancellationToken, Task<Dictionary<string, ArchiveState>>> read,
        Func<IReadOnlyList<string>, CancellationToken, Task> request, Action<RestoreUpdate> report,
        Func<CancellationToken, Task> waitForNextCheck, CancellationToken token)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        report(new(TransferState.CheckingStatus, null, $"Checking {files.Count:N0} files in the folder."));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var states = await read(token);
            token.ThrowIfCancellationRequested();
            var offline = new List<string>();
            int onDisk = 0;
            bool staging = false;
            foreach (string file in files)
            {
                var state = KnownStatus(states, file);
                if (ArchiveStatus.IsOnDisk(state))
                {
                    onDisk++;
                    requested.Remove(file);
                }
                else if (state == ArchiveState.OFL && requested.Add(file))
                    offline.Add(file);
                else if (state == ArchiveState.STG)
                    staging = true;
            }

            string count = $"{onDisk:N0} of {files.Count:N0} files are on disk.";
            if (onDisk == files.Count)
            {
                report(new(TransferState.Ready, null, count));
                return;
            }
            if (offline.Count > 0)
            {
                report(new(TransferState.RequestingTape, null, $"{count} Requesting {offline.Count:N0} files from tape."));
                await request(offline, token);
            }
            report(new(staging ? TransferState.RestoringTape : TransferState.TapeQueue, null, count));
            await waitForNextCheck(token);
        }
    }

    public static ArchiveState KnownStatus(IReadOnlyDictionary<string, ArchiveState> states, string path)
    {
        if (!states.TryGetValue(path, out var state) || state == ArchiveState.Unknown)
            throw new IOException($"SURF did not confirm the disk status of {path}. Refresh the folder and try again.");
        return state;
    }

    public static async Task<ArchiveState> WaitForDisk(Func<CancellationToken, Task<ArchiveState>> read,
        Func<CancellationToken, Task> request, Action<RestoreUpdate> report,
        Func<CancellationToken, Task> waitForNextCheck, CancellationToken token)
    {
        report(new(TransferState.CheckingStatus, null, "Checking the current archive status."));
        bool requested = false;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            ArchiveState state = await read(token);
            token.ThrowIfCancellationRequested();

            if (ArchiveStatus.IsOnDisk(state))
            {
                report(new(TransferState.Ready, state, $"{ArchiveStatus.Label(state)}. Ready to download."));
                return state;
            }

            if (state == ArchiveState.Unknown)
                throw new IOException("SURF did not confirm whether the file is available on disk. Refresh the archive and try again.");

            if (state == ArchiveState.OFL && !requested)
            {
                report(new(TransferState.RequestingTape, state, "Requesting a restore from tape on SURF."));
                await request(token);
                requested = true;
                report(new(TransferState.TapeQueue, state, "Restore requested. Waiting for SURF to stage the file."));
            }
            else
            {
                if (state == ArchiveState.STG)
                    report(new(TransferState.RestoringTape, state, "Checked SURF: restoring the file from tape to disk."));
                else
                    report(new(TransferState.TapeQueue, state, $"Checked SURF: {ArchiveStatus.Label(state)}. Waiting for a disk copy."));
            }

            await waitForNextCheck(token);
        }
    }
}
