using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace SurfArchive;

internal enum PackageStep { Archive = 1, Verify, RemoveOriginals, Upload, RemoteSha, RemoveParts }

internal record PackageMeter(PackageStep Step, long Done, long Total, bool CountItems = false, bool Finished = false);
internal record PackageManifest(string ArchiveRoot, string Destination, string Name, ArchivePart[] Parts);

internal interface IPackageDestination
{
    string Root { get; }
    Task Prepare(string parent, string name, bool resume, CancellationToken token);
    bool HasPart(string name);
    Task Upload(string localPath, IProgress<TransferProgress> progress, CancellationToken token);
    Task<string> Hash(string name, CancellationToken token);
}

internal static class ArchivePackage
{
    public const long PartLimit = 200_000_000;
    private const string ManifestName = "package.json";

    public static string NewName() => $"SURF-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    public static async Task Create(PackagePlan plan, string name, string remoteParent, IPackageDestination target,
        Action<PackageMeter> report, CancellationToken token, long partLimit = PartLimit)
    {
        string folder = Path.Combine(plan.StagingParent, name);
        if (Path.Exists(folder) || partLimit <= 0)
            throw new IOException("The local package folder already exists or the volume size is invalid.");
        Directory.CreateDirectory(folder);
        using (var jobLock = Lock(folder))
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            await target.Prepare(remoteParent, name, resume: false, token);
            var channel = Channel.CreateUnbounded<ArchivePart>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
            using var uploading = CancellationTokenSource.CreateLinkedTokenSource(token);
            long exactTotal = 0;
            var upload = Task.Run(UploadWhileBuilding);
            ArchivePart[] parts;
            try
            {
                parts = WriteTar(folder, plan, hashes, partLimit, report, token, part => channel.Writer.TryWrite(part));
                Interlocked.Exchange(ref exactTotal, parts.Sum(part => part.Size));
                channel.Writer.TryComplete();
                using (var frozen = FreezeParts(folder, parts))
                {
                    VerifyTar(folder, plan, parts, hashes, report, token);
                    SaveManifest(folder, new PackageManifest(target.Root, remoteParent, name, parts));
                    // A failed upload leaves the originals as well as the verified local package.
                    await upload;
                    RemoveOriginals(plan, hashes, report, token);
                    await VerifyRemote(parts, target, report, token);
                }
            }
            finally
            {
                channel.Writer.TryComplete();
                uploading.Cancel();
                try { await upload; }
                catch (Exception) { /* Preserve the original error from packing, verification or upload. */ }
            }

            RemoveLocalParts(folder, parts, report, token);
            File.Delete(Path.Combine(folder, ManifestName));

            async Task UploadWhileBuilding()
            {
                long done = 0;
                await foreach (var part in channel.Reader.ReadAllAsync(uploading.Token))
                {
                    long offset = done;
                    await target.Upload(Path.Combine(folder, part.Name), new Callback<TransferProgress>(progress =>
                        report(new(PackageStep.Upload, offset + progress.Bytes, ProgressTotal(offset + progress.Bytes)))),
                        uploading.Token);
                    done += part.Size;
                    report(new(PackageStep.Upload, done, ProgressTotal(done)));
                }
                report(new(PackageStep.Upload, done, done, Finished: true));
            }

            long ProgressTotal(long done)
            {
                long final = Interlocked.Read(ref exactTotal);
                return final > 0 ? final : Math.Max(plan.Required, done + 1);
            }
        }
        FinishFolder(folder);
    }

    public static async Task Resume(string folder, IPackageDestination target, Action<PackageMeter> report,
        CancellationToken token)
    {
        using (var jobLock = Lock(folder))
        {
            var manifest = ReadManifest(folder);
            if (manifest.ArchiveRoot != target.Root)
                throw new IOException("This package was prepared for a different SURF account.");
            var available = manifest.Parts.Where(part => File.Exists(Path.Combine(folder, part.Name))).ToArray();
            using (var frozen = FreezeParts(folder, available))
            {
                long total = available.Sum(part => part.Size), read = 0;
                report(new(PackageStep.Verify, 0, total));
                using var reader = new VolumeReader(folder, available,
                    amount => report(new(PackageStep.Verify, read += amount, total)), token);
                Drain(reader);
                report(new(PackageStep.Verify, total, total, Finished: true));
                await target.Prepare(manifest.Destination, manifest.Name, resume: true, token);
                await Publish(folder, manifest, target, report, token);
            }
            RemoveLocalParts(folder, manifest.Parts, report, token);
            File.Delete(Path.Combine(folder, ManifestName));
        }
        FinishFolder(folder);
    }

    private static ArchivePart[] WriteTar(string folder, PackagePlan plan, Dictionary<string, string> hashes,
        long partLimit, Action<PackageMeter> report, CancellationToken token, Action<ArchivePart> sealedVolume)
    {
        // Count directories and empty files as one unit so these also move the meter.
        long total = plan.Entries.Sum(item => Math.Max(1, item.Size)), done = 0;
        report(new(PackageStep.Archive, 0, total));
        using var volumes = new VolumeWriter(folder, partLimit, sealedVolume);
        using (var tar = new TarWriter(volumes, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                var attributes = PackageFiles.GetRegularAttributes(item.Path);
                if (attributes.HasFlag(FileAttributes.Directory) != item.IsDirectory)
                    throw new IOException($"The source changed while packing: {item.Path}");
                PackageFiles.EnsureDefaultDataStream(item.Path);
                var entry = new PaxTarEntry(item.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, item.Name)
                {
                    ModificationTime = new DateTimeOffset(item.Modified)
                };
                if (item.IsDirectory)
                {
                    tar.WriteEntry(entry);
                    report(new(PackageStep.Archive, ++done, total));
                    continue;
                }

                using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, FileOptions.SequentialScan);
                if (input.Length != item.Size || File.GetLastWriteTimeUtc(item.Path) != item.Modified)
                    throw new IOException($"The source changed while packing: {item.Path}");
                using var checker = new HashingFileStream(input,
                    count => report(new(PackageStep.Archive, done += count, total)), token);
                entry.DataStream = checker;
                tar.WriteEntry(entry);
                if (checker.ReadBytes != item.Size)
                    throw new IOException($"The source was not fully copied: {item.Path}");
                hashes.Add(item.Name, checker.Finish());
                if (item.Size == 0)
                    report(new(PackageStep.Archive, ++done, total));
            }
        }
        var parts = volumes.Finish();
        report(new(PackageStep.Archive, total, total, Finished: true));
        return parts;
    }

    private static void VerifyTar(string folder, PackagePlan plan, ArchivePart[] parts,
        Dictionary<string, string> hashes, Action<PackageMeter> report, CancellationToken token)
    {
        long total = parts.Sum(part => part.Size), done = 0;
        report(new(PackageStep.Verify, 0, total));
        using var volumes = new VolumeReader(folder, parts, count => report(new(PackageStep.Verify, done += count, total)), token);
        using var reader = new TarReader(volumes, leaveOpen: true);
        var buffer = new byte[1 << 20];
        foreach (var expected in plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            TarEntry? entry = reader.GetNextEntry(copyData: false);
            if (entry is null || entry.Name != expected.Name ||
                entry.EntryType != (expected.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile) ||
                entry.Length != expected.Size)
                throw new IOException($"Could not verify TAR entry {expected.Name}.");
            if (!expected.IsDirectory)
            {
                string digest = entry.DataStream is { } data
                    ? PackageFiles.Hash(data, buffer, token) : Convert.ToHexString(SHA256.HashData([]));
                if (digest != hashes[expected.Name])
                    throw new IOException($"The TAR contents do not match {expected.Name}.");
            }
        }
        if (reader.GetNextEntry(copyData: false) is not null)
            throw new IOException("The TAR archive contains unexpected entries.");
        Drain(volumes); // Checks SHA-256 for the complete bytes of every volume, including TAR padding.
        report(new(PackageStep.Verify, total, total, Finished: true));
    }

    private static void RemoveOriginals(PackagePlan plan, Dictionary<string, string> hashes,
        Action<PackageMeter> report, CancellationToken token)
    {
        long total = plan.Entries.Sum(entry => Math.Max(1, entry.Size)), done = 0;
        report(new(PackageStep.RemoveOriginals, 0, total));
        var buffer = new byte[1 << 20];
        foreach (var entry in plan.Entries.Reverse())
        {
            token.ThrowIfCancellationRequested();
            for (string? parent = Path.GetDirectoryName(entry.Path);
                 parent is not null && !parent.Equals(plan.Source, StringComparison.OrdinalIgnoreCase);
                 parent = Path.GetDirectoryName(parent))
                PackageFiles.GetRegularAttributes(parent);
            if (entry.IsDirectory)
            {
                if (!PackageFiles.GetRegularAttributes(entry.Path).HasFlag(FileAttributes.Directory))
                    throw new IOException($"The original folder changed: {entry.Path}");
                PackageFiles.EnsureDefaultDataStream(entry.Path);
                Directory.Delete(entry.Path, recursive: false);
            }
            else
                PackageFiles.DeleteVerified(entry, hashes[entry.Name], buffer, token,
                    amount => report(new(PackageStep.RemoveOriginals, done += amount, total)));
            if (entry.Size == 0)
                report(new(PackageStep.RemoveOriginals, ++done, total));
        }
        report(new(PackageStep.RemoveOriginals, total, total, Finished: true));
    }

    private static async Task Publish(string folder, PackageManifest manifest, IPackageDestination target,
        Action<PackageMeter> report, CancellationToken token)
    {
        long total = manifest.Parts.Sum(part => part.Size), done = 0;
        report(new(PackageStep.Upload, 0, total));
        foreach (var part in manifest.Parts)
        {
            token.ThrowIfCancellationRequested();
            if (!target.HasPart(part.Name))
            {
                string path = Path.Combine(folder, part.Name);
                if (!File.Exists(path))
                    throw new IOException($"Archive part {part.Name} is missing locally and on SURF.");
                long offset = done;
                await target.Upload(path, new Callback<TransferProgress>(progress =>
                    report(new(PackageStep.Upload, offset + progress.Bytes, total))), token);
            }
            report(new(PackageStep.Upload, done += part.Size, total));
        }
        report(new(PackageStep.Upload, total, total, Finished: true));
        await VerifyRemote(manifest.Parts, target, report, token);
    }

    private static async Task VerifyRemote(ArchivePart[] parts, IPackageDestination target,
        Action<PackageMeter> report, CancellationToken token)
    {
        long total = parts.Sum(part => part.Size), done = 0;
        report(new(PackageStep.RemoteSha, 0, total));
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            string remote = await target.Hash(part.Name, token);
            if (!remote.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SURF SHA-256 does not match {part.Name}. The local archive was kept.");
            report(new(PackageStep.RemoteSha, done += part.Size, total));
        }
        report(new(PackageStep.RemoteSha, total, total, Finished: true));
    }

    private static void RemoveLocalParts(string folder, ArchivePart[] parts,
        Action<PackageMeter> report, CancellationToken token)
    {
        long done = 0;
        report(new(PackageStep.RemoveParts, 0, parts.Length, true));
        var buffer = new byte[1 << 20];
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(folder, part.Name);
            if (File.Exists(path))
                PackageFiles.DeleteVerified(new(path, part.Name, false, part.Size, File.GetLastWriteTimeUtc(path)),
                    part.Sha256, buffer, token);
            report(new(PackageStep.RemoveParts, ++done, parts.Length, true));
        }
        report(new(PackageStep.RemoveParts, parts.Length, parts.Length, true, true));
    }

    private static void Drain(Stream input)
    {
        var buffer = new byte[1 << 20];
        while (input.Read(buffer) > 0) { }
    }

    private static FileStream Lock(string folder) =>
        new(Path.Combine(folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void FinishFolder(string folder)
    {
        File.Delete(Path.Combine(folder, ".lock"));
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    private static void SaveManifest(string folder, PackageManifest manifest)
    {
        using var file = new FileStream(Path.Combine(folder, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, manifest);
        file.Flush(flushToDisk: true);
    }

    public static PackageManifest ReadManifest(string folder)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(folder, ManifestName)))
            ?? throw new IOException("The recovery manifest could not be opened.");
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), manifest.Name, StringComparison.OrdinalIgnoreCase)
            || !manifest.Name.StartsWith("SURF-", StringComparison.Ordinal) || manifest.Parts.Length == 0)
            throw new IOException("This folder is not a verified SURF package.");
        for (int i = 0; i < manifest.Parts.Length; i++)
        {
            var part = manifest.Parts[i];
            string expected = $"data.tar.part-{i + 1:D6}";
            if (part.Name != expected || part.Size <= 0 || part.Size > PartLimit || part.Sha256.Length != 64
                || !part.Sha256.All(Uri.IsHexDigit))
                throw new IOException("The recovery manifest contains an invalid archive part.");
        }
        return manifest;
    }

    private static PartLocks FreezeParts(string folder, ArchivePart[] parts)
    {
        var locks = new PartLocks();
        try
        {
            foreach (var part in parts)
                locks.Handles.Add(File.OpenHandle(Path.Combine(folder, part.Name), FileMode.Open, FileAccess.Read, FileShare.Read));
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    private sealed class PartLocks : IDisposable
    {
        public List<Microsoft.Win32.SafeHandles.SafeFileHandle> Handles { get; } = [];
        public void Dispose()
        {
            foreach (var handle in Handles)
                handle.Dispose();
        }
    }
}
