using System.Security;

namespace SurfArchive;

internal static class LocalFiles
{
    public const string ActiveArchive = @"L:\Archief\Active";

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
