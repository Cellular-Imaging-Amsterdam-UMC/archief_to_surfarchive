using System.Security;

namespace SurfArchive;

internal static class LocalFiles
{
    public const long MinimumDirectUpload = 100_000_000;

    public static bool CanUploadDirectly(IReadOnlyList<ArchiveEntry> selection) =>
        selection.Count > 0 && selection.All(item => !item.IsDirectory && item.Size > MinimumDirectUpload);

    public static bool IsDownloadStagingName(string name) =>
        name.StartsWith(".surfarchive-download-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".surfarchive-folder-", StringComparison.OrdinalIgnoreCase);

    public static bool IsDirectoryNotEmpty(IOException error) => (error.HResult & 0xFFFF) == 145;

    public static string DescribeNonEmptyDirectory(string path, Func<string, bool>? wasScanned = null)
    {
        const int limit = 10;
        try
        {
            var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", ReadEntries).Take(limit + 1).ToArray();
            if (entries.Length == 0)
                return $"A fresh listing of {path}, including hidden items, found no entries. " +
                    "A deferred SMB deletion or a change between the delete and the listing is possible.";
            string found = string.Join(", ", entries.Take(limit).Select(entry =>
            {
                string known = wasScanned is null ? "" : wasScanned(entry.FullName) ? ", listed in source scan" : ", absent from source scan";
                return $"\"{entry.Name}\" ({entry.Attributes}{known})";
            }));
            string more = entries.Length > limit ? " and more" : "";
            return $"Directory inspection of {path} found these entries, including hidden and system entries. {found}{more}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Could not inspect the contents of {path}. {error.Message}";
        }
    }

    public static bool PathsOverlap(string first, string second) =>
        PackageFiles.IsWithin(first, second) || PackageFiles.IsWithin(second, first);

    public static bool CanRemoveResolvedPaths(string directory, IReadOnlyList<ArchiveEntry> items, string[] protectedPaths)
    {
        if (protectedPaths.Length == 0)
            return true;
        var removalPaths = items.Select(item => PackageFiles.ResolvePath(Path.Combine(directory, item.Name))).ToArray();
        return !protectedPaths.Select(PackageFiles.ResolvePath).Any(path =>
            removalPaths.Any(removal => PathsOverlap(removal, path)));
    }

    public const string ArchiveDrive = @"L:\";
    public const string BasicFolder = @"L:\basic";
    public const string ActiveArchive = @"L:\Archief\active";

    private static readonly EnumerationOptions ReadEntries = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public static List<ArchiveEntry> List(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        bool filterFolders = fullPath.Equals(ActiveArchive, StringComparison.OrdinalIgnoreCase);
        var entries = new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", ReadEntries).ToList();
        if (filterFolders)
        {
            // A directory entry can be visible on SMB even when its contents cannot be listed.
            entries = entries.AsParallel().WithDegreeOfParallelism(8)
                .Where(info => info is not DirectoryInfo || CanList(info.FullName)).ToList();
        }

        return entries.Select(info => new ArchiveEntry(info.Name, info is DirectoryInfo,
            info is FileInfo file ? file.Length : 0, info.LastWriteTime)).ToList();
    }

    internal static bool CanList(string path)
    {
        try
        {
            using var items = Directory.EnumerateFileSystemEntries(path, "*", ReadEntries).GetEnumerator();
            items.MoveNext();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static void Remove(string directory, ArchiveEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Name) || entry.Name is "." or ".."
            || entry.Name.EndsWith(' ') || entry.Name.EndsWith('.') || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Invalid Windows file or folder name.");
        string path = Path.Combine(directory, entry.Name);
        var attributes = File.GetAttributes(path);
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (isDirectory != entry.IsDirectory)
            throw new IOException("The item changed since it was selected. Refresh the folder and try again.");
        if (isDirectory)
            Directory.Delete(path, recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
        else
            File.Delete(path);
    }
}
