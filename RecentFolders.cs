using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SurfArchive;

internal sealed record RecentFolder(string Path, string RootPath, string RootLabel);

internal sealed class RecentFolders
{
    private static readonly string Settings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfArchive");
    private readonly string file;
    private readonly StringComparison comparison;
    private readonly List<RecentFolder> locations;

    public RecentFolders(string? archiveRoot)
    {
        string scope = archiveRoot is null ? "windows" : "surf-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(archiveRoot)));
        file = Path.Combine(Settings, "recent-" + scope + ".json");
        comparison = archiveRoot is null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            locations = File.Exists(file) ? JsonSerializer.Deserialize<List<RecentFolder>>(File.ReadAllText(file)) ?? [] : [];
            locations.RemoveAll(item => item is null || string.IsNullOrEmpty(item.Path)
                || string.IsNullOrEmpty(item.RootPath) || string.IsNullOrEmpty(item.RootLabel));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            locations = [];
        }
    }

    public IReadOnlyList<RecentFolder> Items => locations;

    public void Use(RecentFolder folder)
    {
        if (locations.Count > 0 && locations[0] == folder)
            return;
        locations.RemoveAll(item => string.Equals(item.Path, folder.Path, comparison));
        locations.Insert(0, folder);
        if (locations.Count > 15)
            locations.RemoveRange(15, locations.Count - 15);
        Save();
    }

    public void Clear()
    {
        locations.Clear();
        Save();
    }

    private void Save()
    {
        string staging = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Settings);
            File.WriteAllText(staging, JsonSerializer.Serialize(locations));
            File.Move(staging, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staging); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
