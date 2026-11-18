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
}
