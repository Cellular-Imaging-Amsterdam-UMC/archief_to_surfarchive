using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurfArchive;

internal enum ArchiveQueueState { Queued, Paused, Running, Finished, Failed, Cancelled, Interrupted }

internal sealed record ArchiveQueueProgress(int Completed, int Total, TimeSpan Elapsed)
{
    [JsonIgnore]
    public long? StartedAt { get; init; }

    [JsonIgnore]
    public TimeSpan CurrentElapsed => StartedAt is { } started ? Elapsed + Stopwatch.GetElapsedTime(started) : Elapsed;

    public static ArchiveQueueProgress Start(int total, TimeSpan elapsed = default) =>
        new(0, total, elapsed) { StartedAt = Stopwatch.GetTimestamp() };

    public ArchiveQueueProgress Checkpoint(int completed, bool keepRunning) => this with
    {
        Completed = Math.Clamp(completed, Completed, Total),
        Elapsed = CurrentElapsed,
        StartedAt = keepRunning ? Stopwatch.GetTimestamp() : null
    };
}

internal sealed record ArchiveQueueItem(string LocalName, string Name, string Source, string Parent, ArchiveEntry[] Selection,
    string? SourceHash, PackageOptions? Options, ArchiveQueueState State = ArchiveQueueState.Queued,
    string? RecoveryPath = null, string? Detail = null, ArchiveQueueProgress? Progress = null, bool DirectUpload = false)
{
    [JsonIgnore]
    public string RemotePath => ArchiveSession.Child(Parent, Name);

    [JsonIgnore]
    public bool CanResumeUpload => !DirectUpload && RecoveryPath is not null
        && State is ArchiveQueueState.Interrupted or ArchiveQueueState.Failed or ArchiveQueueState.Cancelled;

    [JsonIgnore]
    public string? LocalReferencePath => DirectUpload ? null : PackageContents.LocalReferencePath(Source, Selection);

    [JsonIgnore]
    public int Steps => DirectUpload ? 1 : ArchiveQueue.ArchiveSteps;

    public static ArchiveQueueItem FromSelection(ArchiveSelection selection, PackageOptions options) =>
        new(ArchivePackage.NewName(), selection.Name, selection.Source, selection.Parent, selection.Items, null, options);

    public static ArchiveQueueItem FromUpload(string source, string destination, ArchiveEntry file) =>
        new(ArchivePackage.NewName(), file.Name, source, destination, [file], null, null, DirectUpload: true);

    public ArchiveRequest Prepare(CancellationToken token, ArchiveScanProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (LocalReferencePath is { } reference)
        {
            if (Path.GetFileName(reference).Length > 255)
                throw new IOException("The local folder name is too long for the __sda_ prefix. Shorten it and queue it again.");
            if (Path.Exists(reference))
                throw new IOException($"The local archive reference already exists at {reference}. Choose another source folder or move the existing reference.");
        }
        var plan = PackagePlan.Create(Source, Selection, token, progress, Options!.LocalStagingFolder,
            checkSelectedFiles: SourceHash is null);
        if (SourceHash is { } hash)
        {
            progress?.SetPhase("Comparing with queued selection");
            if (Fingerprint(plan, token) != hash)
                throw new IOException("The source selection changed after it was queued. Review the selection and add it to the queue again.");
        }
        token.ThrowIfCancellationRequested();
        return new(plan, LocalName, Name, Parent, Selection);
    }

    private static string Fingerprint(PackagePlan plan, CancellationToken token)
    {
        using var sha = SHA256.Create();
        using var output = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartArray();
            foreach (var entry in plan.Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                JsonSerializer.Serialize(json, entry);
            }
            json.WriteEndArray();
        }
        output.FlushFinalBlock();
        return Convert.ToHexString(sha.Hash!);
    }
}

internal sealed class ArchiveQueue : IDisposable
{
    public const int ScanSteps = 1;
    public static int ArchiveSteps { get; } = ScanSteps + Enum.GetValues<PackageStep>().Length;
    private const int RecoverySteps = 4;
    private List<ArchiveQueueItem> items = [];
    private ArchiveQueueStore? store;
    public IReadOnlyList<ArchiveQueueItem> Items => items;
    public bool IsAvailable => store is not null;
    public event Action? Changed;

    public void Attach(ArchiveQueueStore storage, List<ArchiveQueueItem> saved)
    {
        store?.Dispose();
        store = storage;
        items = saved;
        Changed?.Invoke();
    }

    public void Detach()
    {
        Dispose();
        items = [];
        Changed?.Invoke();
    }

    public void CheckDestinations(IEnumerable<string> requested)
    {
        var destinations = items.Where(item => item.State is ArchiveQueueState.Queued or ArchiveQueueState.Paused
            or ArchiveQueueState.Running).Select(item => item.RemotePath).ToHashSet(StringComparer.Ordinal);
        foreach (string path in requested)
            if (!destinations.Add(path))
                throw new IOException($"A job for {path} is already in this queue.");
    }

    public void Add(IReadOnlyList<ArchiveQueueItem> requested)
    {
        CheckDestinations(requested.Select(item => item.RemotePath));
        Commit([.. items, .. requested]);
    }

    public ArchiveQueueItem? StartNext()
    {
        if (!IsAvailable || items.Any(item => item.State == ArchiveQueueState.Running))
            return null;
        var next = items.Find(item => item.State == ArchiveQueueState.Queued);
        if (next is null)
            return null;
        var running = next with { State = ArchiveQueueState.Running, Progress = ArchiveQueueProgress.Start(next.Steps) };
        Replace(next, running);
        return running;
    }

    public void TogglePause(ArchiveQueueItem item)
    {
        if (item.State is ArchiveQueueState.Queued or ArchiveQueueState.Paused)
            Replace(item, item with { State = item.State == ArchiveQueueState.Paused ? ArchiveQueueState.Queued : ArchiveQueueState.Paused });
    }

    public void SetRecoveryPath(ArchiveQueueItem item, string path) => Replace(item, item with { RecoveryPath = path });

    public void StartRecovery(ArchiveQueueItem item, string folder, bool restoreReference = false)
    {
        if (!items.Contains(item) || item.DirectUpload
            || item.State is not (ArchiveQueueState.Interrupted or ArchiveQueueState.Failed or ArchiveQueueState.Cancelled)
            || items.Any(other => other.State == ArchiveQueueState.Running))
            throw new IOException("This queue item is no longer available for recovery.");
        CheckDestinations([item.RemotePath]);
        Replace(item, item with
        {
            State = ArchiveQueueState.Running, RecoveryPath = folder, Detail = null,
            Progress = ArchiveQueueProgress.Start(RecoverySteps + (restoreReference ? 1 : 0),
                item.Progress?.CurrentElapsed ?? TimeSpan.Zero)
        });
    }

    public void CompleteSteps(string localName, int completed)
    {
        var current = items.Find(item => item.LocalName == localName && item.State == ArchiveQueueState.Running);
        if (current?.Progress is { } progress && completed > progress.Completed)
            Replace(current, current with { Progress = progress.Checkpoint(completed, keepRunning: true) });
    }

    public void Finish(string localName, ArchiveQueueState state, string? detail = null)
    {
        if (items.Find(item => item.LocalName == localName) is { } current)
        {
            var progress = current.Progress;
            if (progress is not null)
                progress = progress.Checkpoint(state == ArchiveQueueState.Finished ? progress.Total : progress.Completed, keepRunning: false);
            Replace(current, current with { State = state, Detail = detail, Progress = progress });
        }
    }

    public void Remove(ArchiveQueueItem item)
    {
        if (item.State != ArchiveQueueState.Running && items.Contains(item))
            Commit(items.Where(other => other != item).ToList());
    }

    public void RemoveFinished()
    {
        if (items.Any(item => item.State == ArchiveQueueState.Finished))
            Commit(items.Where(item => item.State != ArchiveQueueState.Finished).ToList());
    }

    private void Replace(ArchiveQueueItem current, ArchiveQueueItem next)
    {
        int index = items.IndexOf(current);
        if (index < 0)
            return;
        Commit(items.Select((item, i) => i == index ? next : item).ToList());
    }

    private void Commit(List<ArchiveQueueItem> snapshot)
    {
        if (store is null)
            throw new IOException("The archive queue is unavailable for this account.");
        store.Save(snapshot);
        items = snapshot;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        store?.Dispose();
        store = null;
    }
}
