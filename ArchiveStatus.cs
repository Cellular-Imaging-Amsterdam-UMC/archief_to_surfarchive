namespace SurfArchive;

internal enum ArchiveState
{
    Unknown, REG, DUL, OFL, QUE, STG
}

internal static class ArchiveStatus
{
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

internal static class TapeRestore
{
    public static async Task WaitForDisk(Func<CancellationToken, Task<ArchiveState>> read,
        Func<CancellationToken, Task> request, Action<RestoreUpdate> report, TimeSpan pollInterval, CancellationToken token)
    {
        report(new(TransferState.CheckingStatus, null, "Checking the current archive status."));
        ArchiveState? previous = null;
        bool requested = false;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            ArchiveState state = await read(token);
            token.ThrowIfCancellationRequested();

            if (state is ArchiveState.REG or ArchiveState.DUL)
            {
                report(new(TransferState.Preparing, state, $"{ArchiveStatus.Label(state)}. The file is available for SFTP."));
                return;
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
            else if (state != previous)
            {
                if (state == ArchiveState.STG)
                    report(new(TransferState.RestoringTape, state, "SURF is restoring the file from tape to disk."));
                else
                    report(new(TransferState.TapeQueue, state, "Waiting for SURF to confirm the disk copy is ready."));
            }

            previous = state;
            await Task.Delay(pollInterval, token);
        }
    }
}
