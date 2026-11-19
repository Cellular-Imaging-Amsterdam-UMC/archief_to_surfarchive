namespace SurfArchive;

internal enum ArchiveQueueState { Queued, Paused, Running }

internal sealed class ArchiveQueueItem(ArchiveRequest request, PackageOptions options)
{
    public ArchiveRequest Request { get; } = request;
    public PackageOptions Options { get; } = options;
    public ArchiveQueueState State { get; internal set; }
}

internal sealed class ArchiveQueue
{
    private readonly List<ArchiveQueueItem> items = [];
    public IReadOnlyList<ArchiveQueueItem> Items => items;
    public int PendingCount => items.Count(item => item.State != ArchiveQueueState.Running);

    public event Action? Changed;

    public void CheckDestinations(IReadOnlyList<ArchiveRequest> requests)
    {
        var destinations = items.Select(item => item.Request.RemotePath).ToHashSet(StringComparer.Ordinal);
        foreach (var request in requests)
            if (!destinations.Add(request.RemotePath))
                throw new IOException($"An archive for {request.RemotePath} is already in this queue.");
    }

    public void Add(IReadOnlyList<ArchiveRequest> requests, PackageOptions options)
    {
        CheckDestinations(requests);
        items.AddRange(requests.Select(request => new ArchiveQueueItem(request, options)));
        Changed?.Invoke();
    }

    public ArchiveQueueItem? StartNext()
    {
        if (items.Any(item => item.State == ArchiveQueueState.Running))
            return null;
        var next = items.Find(item => item.State == ArchiveQueueState.Queued);
        if (next is not null)
        {
            next.State = ArchiveQueueState.Running;
            Changed?.Invoke();
        }
        return next;
    }

    public void TogglePause(ArchiveQueueItem item)
    {
        if (item.State == ArchiveQueueState.Running || !items.Contains(item))
            return;
        item.State = item.State == ArchiveQueueState.Paused ? ArchiveQueueState.Queued : ArchiveQueueState.Paused;
        Changed?.Invoke();
    }

    public void Remove(ArchiveQueueItem item)
    {
        if (item.State != ArchiveQueueState.Running && items.Remove(item))
            Changed?.Invoke();
    }

    public void Finish(ArchiveQueueItem item)
    {
        if (item.State == ArchiveQueueState.Running && items.Remove(item))
            Changed?.Invoke();
    }

    public void ClearPending()
    {
        if (items.RemoveAll(item => item.State != ArchiveQueueState.Running) > 0)
            Changed?.Invoke();
    }
}
