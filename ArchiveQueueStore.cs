using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurfArchive;

internal sealed partial class ArchiveQueueStore : IDisposable
{
    private readonly string file;
    private readonly FileStream accountLock;
    private readonly string root;

    public ArchiveQueueStore(string root, string? settingsDirectory = null)
    {
        this.root = root;
        settingsDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfArchive", "Queues");
        Directory.CreateDirectory(settingsDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        file = Path.Combine(settingsDirectory, key + ".json");
        try { accountLock = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new IOException("The archive queue is locked. Close any other app instance using this SURF account and sign in again.", error);
        }
    }

    public List<ArchiveQueueItem> Read()
    {
        List<ArchiveQueueItem> items;
        try
        {
            using var stream = File.OpenRead(file);
            items = JsonSerializer.Deserialize<List<ArchiveQueueItem>>(stream)
                ?? throw new IOException("The saved archive queue is empty or invalid.");
        }
        catch (FileNotFoundException) { return []; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            Validate(item);
            if (!names.Add(item.LocalName))
                throw new IOException("The saved queue has duplicate archive IDs.");
            if (item.State == ArchiveQueueState.Running)
            {
                items[i] = item with { State = ArchiveQueueState.Interrupted, Detail = "The app closed while this archive was running." };
                changed = true;
            }
        }
        if (changed)
            Save(items);
        return items;
    }

    public void Save(IReadOnlyList<ArchiveQueueItem> items)
    {
        string staging = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(output, items);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private void Validate(ArchiveQueueItem item)
    {
        if (item is null || string.IsNullOrEmpty(item.LocalName) || !PackageId().IsMatch(item.LocalName)
            || string.IsNullOrEmpty(item.Name) || string.IsNullOrEmpty(item.Parent)
            || (item.Parent != root && !item.Parent.StartsWith(root + "/", StringComparison.Ordinal))
            || item.Parent.Split('/').Any(part => part is "." or ".." || part.Any(char.IsControl))
            || string.IsNullOrEmpty(item.Source) || !Path.IsPathFullyQualified(item.Source)
            || item.Selection is not { Length: > 0 } || item.Selection.Any(entry => entry is null || string.IsNullOrEmpty(entry.Name))
            || !ArchivePackage.CanArchive(item.Selection)
            || item.SourceHash is not { Length: 64 } || !item.SourceHash.All(Uri.IsHexDigit)
            || item.Options?.Retention?.Fields is not { Years: >= 1 and <= RetentionMetadata.MaximumYears }
            || !Enum.IsDefined(item.State))
            throw new IOException("The saved archive queue contains an invalid entry.");
        ArchivePackage.ValidateFolderName(item.Name);
    }

    [GeneratedRegex(@"\ASURF-[0-9]{8}-[0-9]{6}-[a-f0-9]{32}\z")]
    private static partial Regex PackageId();

    public void Dispose() => accountLock.Dispose();
}
