using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace SurfArchive;

internal enum PackageStep { Archive = 1, Verify, RemoveOriginals, Upload, RemoteSha, RemoveParts, Index }

internal record PackageMeter(PackageStep Step, long Done, long Total, bool CountItems = false,
    bool Finished = false, bool Skipped = false, bool TotalKnown = true,
    TransferState? Phase = null, string? Message = null, bool TotalOnly = false);
internal record PackageManifest(string ArchiveRoot, string Destination, string Name, ArchivePart[] Parts,
    string? FolderName = null, bool IncludesRetention = false, bool SplitZip = false)
{
    [JsonIgnore]
    public string RemoteName => FolderName ?? Name;
}

internal sealed record ArchiveRequest(PackagePlan Plan, string LocalName, string Name, string Parent,
    IReadOnlyList<ArchiveEntry> Items)
{
    public string LocalPath => Path.Combine(Plan.StagingParent, LocalName);
    public string RemotePath => ArchiveSession.Child(Parent, Name);
}

internal interface IPackageDestination
{
    string Root { get; }
    Task Prepare(string parent, string name, bool resume, Action<string> onRetry, CancellationToken token);
    bool HasPart(string name);
    Task Upload(string localPath, ArchivePart part, IProgress<TransferProgress> progress, Action<string> onRetry,
        CancellationToken token);
    Task StageForVerification(ArchivePart[] parts, Action<RestoreUpdate> report, Action<string> onRetry, CancellationToken token);
    Task<string> Hash(string name, Action<string> onRetry, CancellationToken token);
}

internal static class ArchivePackage
{
    public const long PartLimit = 200_000_000;
    private const string ManifestName = "package.json";
    private const int SourceLockLimit = 512;

    public static string NewName() => $"SURF-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    public static bool CanArchive(IReadOnlyList<ArchiveEntry> selection) =>
        selection.Count > 0 && (selection.All(item => item.IsDirectory) || selection.All(item => !item.IsDirectory));

    public static List<ArchiveRequest> PlanSelection(string source, string remoteParent, IReadOnlyList<ArchiveEntry> selection,
        string? filesFolderName, CancellationToken token)
    {
        if (!CanArchive(selection))
            throw new IOException("Select only files or only folders to archive.");

        var requests = new List<ArchiveRequest>();
        if (selection[0].IsDirectory)
        {
            foreach (var folder in selection)
                Add(folder.Name, [folder]);
        }
        else
        {
            Add(filesFolderName ?? "", selection);
        }
        return requests;

        void Add(string name, IReadOnlyList<ArchiveEntry> items)
        {
            ValidateFolderName(name);
            requests.Add(new(PackagePlan.Create(source, items, token), NewName(), name, remoteParent, items));
        }
    }

    public static string VolumeName(string archiveName, int number, bool splitZip = true)
    {
        var stem = new StringBuilder();
        int bytes = 0;
        foreach (var rune in archiveName.TrimStart('.').EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > 220)
                break;
            stem.Append(rune.ToString());
        }
        return splitZip ? $"{stem}.zip.{number:D3}" : $"{stem}-{number:D6}.tar.gz";
    }

    public static void ValidateFolderName(string name)
    {
        ArchiveSession.Child("", name);
        if (Encoding.UTF8.GetByteCount(name) > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException("The source folder name cannot be used on SURF. Shorten or rename it before archiving.");
    }

    public static async Task Create(PackagePlan plan, string name, string archiveName, string remoteParent,
        IPackageDestination target, PackageOptions options, Action<PackageMeter> report,
        CancellationToken token, long partLimit = PartLimit)
    {
        ValidateFolderName(archiveName);
        token.ThrowIfCancellationRequested();
        string folder = Path.Combine(plan.StagingParent, name);
        if (Path.Exists(folder) || partLimit < 4096 || partLimit > PartLimit)
            throw new IOException("The local package folder already exists or the volume size is invalid.");
        Directory.CreateDirectory(folder);
        using (var jobLock = Lock(folder))
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var channel = Channel.CreateUnbounded<ArchivePart>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
            using var uploading = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var originals = options.RemoveOriginals ? new VerifiedFiles() : null;
            using var frozen = new VerifiedFiles();
            long exactTotal = 0;
            var upload = Task.Run(UploadWhileBuilding);
            ArchivePart[] parts;
            try
            {
                var volumes = WriteZip(folder, archiveName, plan, hashes, originals, partLimit, report, token,
                    part => channel.Writer.TryWrite(part));
                frozen.HoldParts(folder, volumes);
                VerifyZip(plan, volumes, frozen, hashes, report, token);
                var contents = PackageContents.Write(folder, archiveName, plan, volumes, options.Retention, report, token, frozen.Hold);
                parts = [.. volumes, .. contents];
                SaveManifest(folder, new PackageManifest(target.Root, remoteParent, name, parts, archiveName,
                    IncludesRetention: true, SplitZip: true));
                long totalSize = parts.Sum(part => part.Size);
                Interlocked.Exchange(ref exactTotal, totalSize);
                report(new(PackageStep.Upload, 0, totalSize, TotalOnly: true));
                foreach (var item in contents)
                    channel.Writer.TryWrite(item);
                channel.Writer.TryComplete();
                await upload;
                // Keep the originals until all remote parts and metadata files match.
                await VerifyRemote(parts, target, ReportStage, token);
                if (originals is not null)
                    RemoveOriginals(plan, hashes, originals, report, token);
                else
                    report(new(PackageStep.RemoveOriginals, 0, 0, CountItems: true, Finished: true, Skipped: true));
            }
            finally
            {
                channel.Writer.TryComplete();
                uploading.Cancel();
                try { await upload; }
                catch (Exception) { /* Preserve the original error from packing, verification or upload. */ }
            }

            RemoveLocalParts(folder, parts, frozen, report, token);
            File.Delete(Path.Combine(folder, ManifestName));

            async Task UploadWhileBuilding()
            {
                long done = 0;
                try
                {
                    await target.Prepare(remoteParent, archiveName, resume: false,
                        message => ReportUpload(0, TransferState.Retrying, message), uploading.Token);
                    while (true)
                    {
                        if (!channel.Reader.TryRead(out var part))
                        {
                            if (!channel.Reader.Completion.IsCompleted)
                                ReportUpload(done, TransferState.WaitingForParts);
                            if (!await channel.Reader.WaitToReadAsync(uploading.Token))
                                break;
                            continue;
                        }

                        await UploadPart(folder, part, target, done, ReportUpload, uploading.Token);
                        done += part.Size;
                        ReportUpload(done);
                    }
                    report(new(PackageStep.Upload, done, done, Finished: true));
                }
                catch (Exception error) when (!uploading.IsCancellationRequested)
                {
                    ReportUpload(done, TransferState.UploadStopped, error.Message);
                    throw;
                }
            }

            void ReportUpload(long done, TransferState? phase = null, string? message = null)
            {
                long final = Interlocked.Read(ref exactTotal);
                ReportStage(new(PackageStep.Upload, done, final, TotalKnown: final > 0, Phase: phase, Message: message));
            }

            void ReportStage(PackageMeter meter)
            {
                if (meter.Phase is TransferState.Retrying or TransferState.UploadStopped
                    or TransferState.RequestingTape or TransferState.TapeQueue or TransferState.RestoringTape)
                    originals?.Dispose();
                report(meter);
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
            using var frozen = new VerifiedFiles();
            frozen.HoldParts(folder, available);
            long total = available.Sum(part => part.Size), read = 0;
            report(new(PackageStep.Verify, 0, total));
            VerifyParts(available, frozen, amount => report(new(PackageStep.Verify, read += amount, total)), token);
            report(new(PackageStep.Verify, total, total, Finished: true));
            await target.Prepare(manifest.Destination, manifest.RemoteName, resume: true, message =>
                report(new(PackageStep.Upload, 0, 0, TotalKnown: false, Phase: TransferState.Retrying, Message: message)), token);
            await Publish(folder, manifest, target, report, token);
            RemoveLocalParts(folder, manifest.Parts, frozen, report, token);
            File.Delete(Path.Combine(folder, ManifestName));
        }
        FinishFolder(folder);
    }

    private static ArchivePart[] WriteZip(string folder, string archiveName, PackagePlan plan, Dictionary<string, string> hashes,
        VerifiedFiles? originals, long partLimit, Action<PackageMeter> report, CancellationToken token, Action<ArchivePart> sealedVolume)
    {
        bool countItems = plan.Bytes == 0;
        long total = countItems ? plan.Entries.Count : plan.Bytes, done = 0;
        // Keep a bounded number of read handles, prioritizing the files with the most data to reread.
        var protect = originals is null ? null : plan.Entries.Where(item => !item.IsDirectory && item.Size > 0)
            .OrderByDescending(item => item.Size).Take(SourceLockLimit).Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        report(new(PackageStep.Archive, 0, total, countItems));
        using var volumes = new VolumeWriter(folder, archiveName, partLimit, sealedVolume);
        using (var zip = new ZipArchive(volumes, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            foreach (var item in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                var attributes = PackageFiles.GetRegularAttributes(item.Path);
                if (attributes.HasFlag(FileAttributes.Directory) != item.IsDirectory)
                    throw new IOException($"The source changed while packing: {item.Path}");
                PackageFiles.EnsureDefaultDataStream(item.Path);
                var entry = zip.CreateEntry(item.Name, item.IsDirectory ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                var modified = new DateTimeOffset(item.Modified).ToLocalTime();
                if (modified.Year is >= 1980 and <= 2107)
                    entry.LastWriteTime = modified;
                if (item.IsDirectory)
                {
                    entry.ExternalAttributes = (int)FileAttributes.Directory;
                    if (countItems)
                        report(new(PackageStep.Archive, ++done, total, countItems));
                    continue;
                }

                bool keep = protect?.Contains(item.Name) == true;
                var share = keep ? FileShare.Read | FileShare.Delete : FileShare.Read;
                SafeFileHandle? input = File.OpenHandle(item.Path, FileMode.Open, FileAccess.Read, share, FileOptions.SequentialScan);
                try
                {
                    PackageFiles.CheckSource(item, input);
                    using var checker = new HashingFileStream(input,
                        count => report(new(PackageStep.Archive, done += count, total)), token);
                    using (var output = entry.Open())
                        checker.CopyTo(output, 1 << 20);
                    if (checker.ReadBytes != item.Size)
                        throw new IOException($"The source was not fully copied: {item.Path}");
                    hashes.Add(item.Name, checker.Finish());
                    if (keep)
                    {
                        originals!.Hold(item.Name, input);
                        input = null;
                    }
                }
                finally { input?.Dispose(); }
                if (countItems)
                    report(new(PackageStep.Archive, ++done, total, countItems));
            }
        }
        var parts = volumes.Finish();
        report(new(PackageStep.Archive, total, total, countItems, Finished: true));
        return parts;
    }

    private static void VerifyZip(PackagePlan plan, ArchivePart[] parts, VerifiedFiles frozen,
        Dictionary<string, string> hashes, Action<PackageMeter> report, CancellationToken token)
    {
        long total = checked(parts.Sum(part => part.Size) + plan.Bytes), done = 0;
        report(new(PackageStep.Verify, 0, total));
        using var volumes = new ZipVolumeReader(parts, frozen.Get, token);
        using var zip = new ZipArchive(volumes, ZipArchiveMode.Read);
        if (zip.Entries.Count != plan.Entries.Count)
            throw new IOException("The ZIP archive has an unexpected number of entries.");
        // ZipArchive reads the central directory first. Start sequential part checks as entry decoding begins.
        volumes.StartVerification(Count);
        var buffer = new byte[1 << 20];
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var expected = plan.Entries[i];
            var entry = zip.Entries[i];
            if (entry.FullName != expected.Name || entry.Length != expected.Size)
                throw new IOException($"Could not verify ZIP entry {expected.Name}.");
            if (!expected.IsDirectory)
            {
                using var data = entry.Open();
                string digest = PackageFiles.Hash(data, buffer, token, Count);
                if (digest != hashes[expected.Name])
                    throw new IOException($"The ZIP contents do not match {expected.Name}.");
            }
        }
        volumes.CompleteVerification();
        report(new(PackageStep.Verify, total, total, Finished: true));

        void Count(int bytes) => report(new(PackageStep.Verify, done += bytes, total));
    }

    private static void VerifyParts(ArchivePart[] parts, VerifiedFiles frozen, Action<int> progress, CancellationToken token)
    {
        var buffer = new byte[1 << 20];
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            var handle = frozen.Get(part.Name);
            if (RandomAccess.GetLength(handle) != part.Size)
                throw new IOException($"The size of {part.Name} changed.");
            using var input = new HashingFileStream(handle, progress, token);
            while (input.Read(buffer) > 0) { }
            if (input.ReadBytes != part.Size || !input.Finish().Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Local SHA-256 mismatch for {part.Name}.");
        }
    }

    private static void RemoveOriginals(PackagePlan plan, Dictionary<string, string> hashes,
        VerifiedFiles originals, Action<PackageMeter> report, CancellationToken token)
    {
        var progress = new CleanupProgress(PackageStep.RemoveOriginals, plan.Entries.Count, report);
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
            {
                using var verified = originals.Take(entry.Name);
                PackageFiles.DeleteVerified(entry, hashes[entry.Name], buffer, verified, token);
            }
            progress.Processed();
        }
        progress.Complete();
    }

    private static async Task Publish(string folder, PackageManifest manifest, IPackageDestination target,
        Action<PackageMeter> report, CancellationToken token)
    {
        var missing = manifest.Parts.Where(part => !target.HasPart(part.Name)).ToArray();
        long total = missing.Sum(part => part.Size), done = 0;
        if (missing.Length == 0)
            report(new(PackageStep.Upload, 0, 0, Finished: true, Skipped: true));
        else
        {
            report(new(PackageStep.Upload, 0, total));
            foreach (var part in missing)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(folder, part.Name);
                if (!File.Exists(path))
                    throw new IOException($"Archive part {part.Name} is missing locally and on SURF.");
                try
                {
                    await UploadPart(folder, part, target, done, ReportUpload, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    ReportUpload(done, TransferState.UploadStopped, error.Message);
                    throw;
                }
                ReportUpload(done += part.Size);
            }
            report(new(PackageStep.Upload, total, total, Finished: true));
        }
        await VerifyRemote(manifest.Parts, target, report, token);

        void ReportUpload(long bytes, TransferState? phase = null, string? message = null) =>
            report(new(PackageStep.Upload, bytes, total, Phase: phase, Message: message));
    }

    private static async Task UploadPart(string folder, ArchivePart part, IPackageDestination target, long offset,
        Action<long, TransferState?, string?> report, CancellationToken token)
    {
        report(offset, TransferState.Transferring, null);
        await target.Upload(Path.Combine(folder, part.Name), part, new Callback<TransferProgress>(progress =>
            report(offset + progress.Bytes, null, null)),
            message => report(offset, TransferState.Retrying, message), token);
    }

    private static async Task VerifyRemote(ArchivePart[] parts, IPackageDestination target,
        Action<PackageMeter> report, CancellationToken token)
    {
        long total = parts.Sum(part => part.Size), done = 0;
        report(new(PackageStep.RemoteSha, 0, total));
        await target.StageForVerification(parts, update =>
        {
            if (update.Phase is TransferState.RequestingTape or TransferState.TapeQueue or TransferState.RestoringTape)
                report(new(PackageStep.RemoteSha, 0, total, Phase: update.Phase, Message: update.Message));
        }, OnRetry, token);
        report(new(PackageStep.RemoteSha, 0, total, Phase: TransferState.Transferring));
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            string remote = await target.Hash(part.Name, OnRetry, token);
            if (!remote.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SURF SHA-256 does not match {part.Name}. The local archive was kept.");
            report(new(PackageStep.RemoteSha, done += part.Size, total));
        }
        report(new(PackageStep.RemoteSha, total, total, Finished: true));

        void OnRetry(string message) =>
            report(new(PackageStep.RemoteSha, done, total, Phase: TransferState.Retrying, Message: message));
    }

    private static void RemoveLocalParts(string folder, ArchivePart[] parts, VerifiedFiles frozen,
        Action<PackageMeter> report, CancellationToken token)
    {
        var progress = new CleanupProgress(PackageStep.RemoveParts, parts.Length, report);
        var buffer = new byte[1 << 20];
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(folder, part.Name);
            using var verified = frozen.Take(part.Name);
            if (Path.Exists(path))
                PackageFiles.DeleteVerified(new(path, part.Name, false, part.Size, File.GetLastWriteTimeUtc(path)),
                    part.Sha256, buffer, verified, token);
            progress.Processed();
        }
        progress.Complete();
    }

    private sealed class CleanupProgress
    {
        private readonly PackageStep step;
        private readonly int total;
        private readonly Action<PackageMeter> report;
        private long last = Stopwatch.GetTimestamp();
        private int done;

        public CleanupProgress(PackageStep step, int total, Action<PackageMeter> report)
        {
            this.step = step;
            this.total = total;
            this.report = report;
            report(new(step, 0, total, CountItems: true));
        }

        public void Processed()
        {
            done++;
            if (Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(300))
            {
                last = Stopwatch.GetTimestamp();
                report(new(step, done, total, CountItems: true));
            }
        }

        public void Complete() => report(new(step, total, total, CountItems: true, Finished: true));
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
            || !manifest.Name.StartsWith("SURF-", StringComparison.Ordinal) || manifest.Parts is not { Length: > 0 })
            throw new IOException("This folder is not a verified SURF package.");
        if ((manifest.IncludesRetention && manifest.FolderName is null) || (manifest.SplitZip && !manifest.IncludesRetention))
            throw new IOException("The recovery manifest has invalid package metadata.");
        string[] sidecars = manifest.IncludesRetention
            ? [PackageContents.TextName, PackageContents.HtmlName, RetentionMetadata.FileName]
            : [PackageContents.TextName, PackageContents.HtmlName];
        int volumeCount = manifest.Parts.Length;
        if (manifest.FolderName is not null)
        {
            ValidateFolderName(manifest.FolderName);
            volumeCount -= sidecars.Length;
            if (volumeCount < 1)
                throw new IOException("The recovery manifest is missing archive volumes or contents files.");
        }
        for (int i = 0; i < manifest.Parts.Length; i++)
        {
            var part = manifest.Parts[i];
            string expected = manifest.FolderName is null ? $"data.tar.part-{i + 1:D6}"
                : i < volumeCount ? VolumeName(manifest.FolderName, i + 1, manifest.SplitZip)
                : sidecars[i - volumeCount];
            if (part is null || part.Name != expected || part.Size <= 0 ||
                (i < volumeCount && part.Size > PartLimit) || part.Sha256 is not { Length: 64 }
                || !part.Sha256.All(Uri.IsHexDigit))
                throw new IOException("The recovery manifest contains an invalid archive part.");
        }
        return manifest;
    }

    private sealed class VerifiedFiles : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);
        private bool disposed;

        public void Hold(string name, SafeFileHandle handle)
        {
            lock (handles)
            {
                if (disposed)
                    handle.Dispose();
                else
                    handles.Add(name, handle);
            }
        }
        public SafeFileHandle Get(string name) => handles[name];
        public SafeFileHandle? Take(string name) => handles.Remove(name, out var handle) ? handle : null;

        public void HoldParts(string folder, ArchivePart[] parts)
        {
            foreach (var part in parts)
                Hold(part.Name, File.OpenHandle(Path.Combine(folder, part.Name), FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete));
        }

        public void Dispose()
        {
            lock (handles)
            {
                disposed = true;
                foreach (var handle in handles.Values)
                    handle.Dispose();
                handles.Clear();
            }
        }
    }
}
