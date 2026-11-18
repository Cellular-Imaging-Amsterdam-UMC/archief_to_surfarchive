namespace SurfArchive;

internal sealed record FolderDownloadFile(string RemotePath, long Size);

internal sealed record FolderDownload(string Name, IReadOnlyList<FolderDownloadFile> Files)
{
    public long Bytes => Files.Sum(file => file.Size);

    public static FolderDownload FromEntries(string path, string name, IReadOnlyList<ArchiveEntry> entries, CancellationToken token)
    {
        ValidateWindowsName(name);
        if (entries.Any(entry => entry.IsDirectory))
            throw new IOException("This SURF folder contains subfolders. Open it and select its files or a folder without subfolders.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FolderDownloadFile>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            ValidateWindowsName(entry.Name);
            if (!names.Add(entry.Name))
                throw new IOException($"Windows cannot copy items with the same name ignoring case in {path}.");
            files.Add(new(ArchiveSession.Child(path, entry.Name), entry.Size));
        }
        return new(name, files);
    }

    public string Destination(string parent)
    {
        string target = Path.Combine(parent, Name);
        if (Path.Exists(target))
            throw new IOException($"A local file or folder named '{Name}' already exists.");
        return target;
    }

    public async Task CopyTo(string parent, Func<FolderDownloadFile, string, IProgress<TransferProgress>, CancellationToken, Task> copy,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        string target = Destination(parent);
        long total = Bytes, done = 0;
        if (PackageFiles.AvailableBytes(parent) < (ulong)total)
            throw new IOException("There is not enough free disk space for this folder.");
        string staging = Path.Combine(parent, $".surfarchive-folder-{Guid.NewGuid():N}");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report(new(Name, 0, total));
            foreach (var file in Files)
            {
                token.ThrowIfCancellationRequested();
                long offset = done;
                await copy(file, staging, new Callback<TransferProgress>(update =>
                    progress.Report(new(Name, offset + update.Bytes, total))), token);
                progress.Report(new(Name, done += file.Size, total));
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in Files)
                    File.Delete(Path.Combine(staging, file.RemotePath[(file.RemotePath.LastIndexOf('/') + 1)..]));
                Directory.Delete(staging);
            }
        }
    }

    public static void ValidateWindowsName(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ');
        bool device = stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]));
        if (name.Length is 0 or > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ')
            || name.EndsWith('.') || device)
            throw new IOException($"Windows does not support the filename '{name}'.");
    }
}
