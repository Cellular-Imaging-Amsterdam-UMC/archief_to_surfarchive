using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurfArchive;

internal enum ArchiveQueueState { Queued, Paused, Running, Finished, Failed, Cancelled, Interrupted }

internal sealed record ArchiveQueueItem(string LocalName, string Name, string Source, string Parent, ArchiveEntry[] Selection,
    string SourceHash, PackageOptions Options, ArchiveQueueState State = ArchiveQueueState.Queued,
    string? RecoveryPath = null, string? Detail = null)
{
    [JsonIgnore]
    public string RemotePath => ArchiveSession.Child(Parent, Name);

    public static ArchiveQueueItem FromRequest(ArchiveRequest request, PackageOptions options, CancellationToken token) =>
        new(request.LocalName, request.Name, request.Plan.Source, request.Parent, request.Items.ToArray(),
            Fingerprint(request.Plan, token), options);

    public ArchiveRequest Prepare(CancellationToken token)
    {
        var plan = PackagePlan.Create(Source, Selection, token);
        if (Fingerprint(plan, token) != SourceHash)
            throw new IOException("The source selection changed after it was queued. Review the selection and add it to the queue again.");
        if (Options.LocalStagingFolder is { } local)
            plan = plan.WithPreferredStaging(local, PackageFiles.LocalStagingFreeBytes(local));
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
                throw new IOException($"An archive for {path} is already in this queue.");
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
        var running = next with { State = ArchiveQueueState.Running };
        Replace(next, running);
        return running;
    }

    public void TogglePause(ArchiveQueueItem item)
    {
        if (item.State is ArchiveQueueState.Queued or ArchiveQueueState.Paused)
            Replace(item, item with { State = item.State == ArchiveQueueState.Paused ? ArchiveQueueState.Queued : ArchiveQueueState.Paused });
    }

    public void SetRecoveryPath(ArchiveQueueItem item, string path) => Replace(item, item with { RecoveryPath = path });

    public void Finish(string localName, ArchiveQueueState state, string? detail = null)
    {
        if (items.Find(item => item.LocalName == localName) is { } current)
            Replace(current, current with { State = state, Detail = detail });
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
