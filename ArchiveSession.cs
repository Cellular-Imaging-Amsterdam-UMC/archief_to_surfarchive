using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SurfArchive;

internal record ArchiveEntry(string Name, bool IsDirectory, long Size, DateTime Modified,
    ArchiveState? Status = null, bool? HasSubfolders = null);
internal record FolderArchiveStatus(string Name, ArchiveState State, bool? HasSubfolders);
internal record TransferProgress(string Name, long Bytes, long Total, bool TotalKnown = true);

internal sealed class ArchiveSession : IDisposable
{
    public const string Host = "archive.surf.nl";
    private static readonly HashSet<string> HostKeys = new(StringComparer.Ordinal)
    {
        "8xz+VxE2tqtaBP7A3Tpvaa9D13u6hEi/UzW72BEJzKE",
        "V43wXq1bF/Rqe+W5pgz+HyYN513eEoezYCrXblCjyug",
        "XCMb5KRubzN78e6/4uGEOBXQbYwuXn9w91qs5OIk610"
    };

    private readonly Credentials credentials;
    private readonly SftpClient sftp;
    public string Root { get; }

    public ArchiveSession(Credentials credentials)
    {
        if (!Regex.IsMatch(credentials.Username, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z"))
            throw new ArgumentException("Invalid SURF username.");
        this.credentials = credentials;
        Root = "/archive/" + credentials.Username;
        sftp = CreateSftpClient();
        try
        {
            sftp.Connect();
            sftp.ChangeDirectory(Root);
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
    }

    private SftpClient CreateSftpClient()
    {
        var client = Configure(new SftpClient(Connection()));
        client.OperationTimeout = TimeSpan.FromSeconds(45);
        return client;
    }

    private ConnectionInfo Connection()
    {
        return new ConnectionInfo(Host, 22, credentials.Username,
            new PasswordAuthenticationMethod(credentials.Username, credentials.Password))
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private static T Configure<T>(T client) where T : BaseClient
    {
        client.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // Verify the host before sending credentials. Fingerprints are published by SURF.
        client.HostKeyReceived += (_, args) => args.CanTrust = HostKeys.Contains(args.FingerPrintSHA256);
        return client;
    }

    private SshClient OpenShell()
    {
        var client = Configure(new SshClient(Connection()));
        try
        {
            client.Connect();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<SshClient> OpenShellAsync(CancellationToken token)
    {
        var client = Configure(new SshClient(Connection()));
        try
        {
            await client.ConnectAsync(token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Reconnect()
    {
        if (!sftp.IsConnected)
            sftp.Connect();
    }

    public async Task<List<ArchiveEntry>> List(string directory)
    {
        Reconnect();
        return await ListEntries(sftp, directory, strict: false, CancellationToken.None);
    }

    private static async Task<List<ArchiveEntry>> ListEntries(SftpClient client, string directory, bool strict,
        CancellationToken token)
    {
        var entries = new List<ArchiveEntry>();
        await foreach (var entry in client.ListDirectoryAsync(directory, token))
        {
            if (entry.Name is "." or "..")
                continue;
            if (entry.Name.Any(char.IsControl) || entry.IsSymbolicLink || (!entry.IsDirectory && !entry.IsRegularFile))
            {
                if (strict)
                    throw new IOException($"This SURF folder contains a link or another unsupported entry: {directory}.");
                continue;
            }
            entries.Add(new(entry.Name, entry.IsDirectory, entry.Length, entry.LastWriteTime));
        }
        return entries;
    }

    public static string Child(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Any(char.IsControl))
            throw new ArgumentException("Invalid file or folder name.");
        return parent + "/" + name;
    }

    public void MakeDirectory(string directory)
    {
        Reconnect();
        sftp.CreateDirectory(directory);
    }

    public async Task<Dictionary<string, ArchiveState>> ReadStatuses(string directory,
        IReadOnlyList<ArchiveEntry> entries, CancellationToken token)
    {
        var paths = entries.Where(entry => !entry.IsDirectory).Select(entry => Child(directory, entry.Name)).ToArray();
        if (paths.Length == 0)
            return new(StringComparer.Ordinal);

        using var ssh = await OpenShellAsync(token);
        var statuses = await ReadPathStatuses(ssh, paths, token);
        return statuses.Where(row => row.Key.StartsWith(directory + "/", StringComparison.Ordinal))
            .ToDictionary(row => row.Key[(directory.Length + 1)..], row => row.Value, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, ArchiveState>> ReadPathStatuses(SshClient ssh,
        IEnumerable<string> paths, CancellationToken token)
    {
        var states = new Dictionary<string, ArchiveState>(StringComparer.Ordinal);
        foreach (string command in PathCommands("daattr -a path,state -d ,", paths, token))
        {
            string output = await RunAsync(ssh, command, token, timeoutSeconds: 60);
            foreach (var row in ArchiveStatus.ParseRows(output))
                states[row.Key] = row.Value;
        }
        return states;
    }

    private static IEnumerable<string> PathCommands(string command, IEnumerable<string> paths, CancellationToken token)
    {
        var batch = new StringBuilder(command);
        int bytes = 0;
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string arg = " " + Quote(path);
            int size = Encoding.UTF8.GetByteCount(arg);
            if (bytes > 0 && bytes + size > 32_000)
            {
                yield return batch.ToString();
                batch.Clear().Append(command);
                bytes = 0;
            }
            batch.Append(arg);
            bytes += size;
        }
        if (bytes > 0)
            yield return batch.ToString();
    }

    public async Task<FolderDownload> ScanFolder(string path, string name, CancellationToken token)
    {
        using var client = CreateSftpClient();
        await client.ConnectAsync(token);
        var root = await client.GetAsync(path, token);
        if (!root.IsDirectory || root.IsSymbolicLink)
            throw new IOException($"The SURF folder is no longer a regular directory: {path}.");
        var entries = await ListEntries(client, path, strict: true, token);
        return FolderDownload.FromEntries(path, name, entries, token);
    }

    public async Task ReadFolderStatuses(string directory, IReadOnlyList<ArchiveEntry> entries,
        IProgress<FolderArchiveStatus> progress, CancellationToken token)
    {
        var folders = entries.Where(entry => entry.IsDirectory).ToArray();
        if (folders.Length == 0)
            return;
        using var client = CreateSftpClient();
        await client.ConnectAsync(token);
        using var ssh = await OpenShellAsync(token);
        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            bool? hasSubfolders = null;
            try
            {
                string path = Child(directory, folder.Name);
                var children = await ListEntries(client, path, strict: false, token);
                hasSubfolders = children.Any(entry => entry.IsDirectory);
                var files = children.Where(entry => !entry.IsDirectory).Select(entry => Child(path, entry.Name)).ToArray();
                var states = await ReadPathStatuses(ssh, files, token);
                progress.Report(new(folder.Name, ArchiveStatus.FolderState(files.Select(file => states.GetValueOrDefault(file))),
                    hasSubfolders));
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is SshException or IOException)
            {
                progress.Report(new(folder.Name, ArchiveState.Unknown, hasSubfolders));
            }
        }
    }

    public async Task<int> CountFolderFilesToRestore(FolderDownload folder, CancellationToken token)
    {
        if (folder.Files.Count == 0)
            return 0;
        using var ssh = await OpenShellAsync(token);
        var paths = folder.Files.Select(file => file.RemotePath).ToArray();
        var states = await ReadPathStatuses(ssh, paths, token);
        return paths.Count(path => !ArchiveStatus.IsOnDisk(TapeRestore.KnownStatus(states, path)));
    }

    public async Task RestoreFolder(FolderDownload folder, Action<RestoreUpdate> report, CancellationToken token)
    {
        if (folder.Files.Count == 0)
            return;
        using var ssh = await OpenShellAsync(token);
        var paths = folder.Files.Select(file => file.RemotePath).ToArray();
        await TapeRestore.WaitForFolder(paths, ct => ReadPathStatuses(ssh, paths, ct), async (offline, ct) =>
        {
            foreach (string command in PathCommands("daget -a", offline, ct))
                await RunAsync(ssh, command, ct, timeoutSeconds: 90);
        }, report, ct => Task.Delay(RestorePolling.ManualInterval, ct), token);
    }

    public async Task DownloadFolder(FolderDownload folder, string destination,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        Reconnect();
        using var ssh = folder.Files.Count > 0 ? await OpenShellAsync(token) : null;
        await folder.CopyTo(destination, async (file, local, itemProgress, ct) =>
        {
            var state = await ReadState(ssh!, Quote(file.RemotePath), ct);
            if (!ArchiveStatus.IsOnDisk(state))
                throw new IOException($"A file is no longer on disk: {file.RemotePath}. Restore the folder before retrying.");
            await CopyToLocal(sftp, file.RemotePath, local, itemProgress, ct, expectedSize: file.Size);
        }, progress, token);
    }

    public void Remove(string directory, ArchiveEntry entry)
    {
        if (directory != Root && !directory.StartsWith(Root + "/", StringComparison.Ordinal))
            throw new IOException("The folder is outside the personal SURF archive.");
        Child(directory, entry.Name);
        string item = Quote("./" + entry.Name);
        using var ssh = OpenShell();
        string type = entry.IsDirectory ? "-d" : "-f";
        string remove = entry.IsDirectory ? "rm -r --one-file-system -- " : "rm -- ";
        Run(ssh, $"cd -- {Quote(Root)} && archive_root=$(pwd -P) && cd -- {Quote(directory)} && " +
            "case \"$(pwd -P)/\" in \"$archive_root/\"*) ;; *) exit 4 ;; esac && " +
            $"test ! -L {item} && test {type} {item} && {remove}{item}", timeoutSeconds: 300);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string Run(SshClient ssh, string commandText, int timeoutSeconds = 30)
    {
        using var command = ssh.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        string output = command.Execute().Trim();
        if (command.ExitStatus != 0)
            throw new IOException($"SURF returned an error (exit {command.ExitStatus}): {command.Error.Trim()}");
        return output;
    }

    private static async Task<string> RunAsync(SshClient ssh, string commandText,
        CancellationToken token, int timeoutSeconds = 30)
    {
        using var command = ssh.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        await command.ExecuteAsync(token);
        if (command.ExitStatus != 0)
            throw new IOException($"SURF returned an error (exit {command.ExitStatus}): {command.Error.Trim()}");
        return command.Result;
    }

    private static async Task CopyUp(SftpClient client, Stream input, string destination, string name,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        long size = input.Length;
        var report = new Callback<UploadFileProgressReport>(value =>
            progress.Report(new(name, checked((long)value.TotalBytesUploaded), size)));
        progress.Report(new(name, 0, size));
        await client.UploadFileAsync(input, destination, canOverride: false, report, token);
        if ((await client.GetAttributesAsync(destination, token)).Size != size)
            throw new IOException("The file size on SURF differs from the source.");
        progress.Report(new(name, size, size));
    }

    public async Task Upload(string source, string directory, IProgress<TransferProgress> progress, CancellationToken token,
        long minimumSizeExclusive = -1)
    {
        Reconnect();
        string name = Path.GetFileName(source);
        string destination = Child(directory, name);
        if (sftp.Exists(destination))
            throw new IOException($"A file or folder named '{name}' already exists on SURF.");
        string staging = Child(directory, ".surfarchive-upload-" + Guid.NewGuid().ToString("N"));
        using var input = OpenSource(source);
        if (input.Length <= minimumSizeExclusive)
            throw new IOException("Direct uploads require files larger than 100 MB. Use Archive to SURF instead.");

        try
        {
            await CopyUp(sftp, input, staging, name, progress, token);
            token.ThrowIfCancellationRequested();
            // Standard SFTP rename refuses to overwrite an existing target.
            await sftp.RenameFileAsync(staging, destination, CancellationToken.None);
        }
        catch (Exception failure)
        {
            try
            {
                Reconnect();
                try { sftp.DeleteFile(staging); }
                catch (SftpPathNotFoundException) { }
            }
            catch (Exception cleanup)
            {
                throw new IOException($"Could not remove the temporary SURF file {staging}: {cleanup.Message}", failure);
            }
            throw;
        }
    }

    public async Task<ArchiveState> MonitorRestore(string source, Action<RestoreUpdate> report,
        Func<CancellationToken, Task> waitForNextCheck, CancellationToken token)
    {
        using var ssh = await OpenShellAsync(token);
        string quoted = Quote(source);
        return await TapeRestore.WaitForDisk(
            ct => ReadState(ssh, quoted, ct),
            async ct => { await RunAsync(ssh, "daget -a " + quoted, ct, timeoutSeconds: 90); },
            report, waitForNextCheck, token);
    }

    private static async Task<ArchiveState> ReadState(SshClient ssh, string quoted, CancellationToken token) =>
        ArchiveStatus.Parse(await RunAsync(ssh, "daattr -a state " + quoted, token));

    public async Task<ArchiveState> Download(string source, string localDirectory,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        ArchiveState state;
        using (var ssh = await OpenShellAsync(token))
            state = await ReadState(ssh, Quote(source), token);
        if (!ArchiveStatus.IsOnDisk(state))
            throw new IOException($"SURF reports {ArchiveStatus.Label(state)}. Refresh the list or restore the file from tape first.");

        token.ThrowIfCancellationRequested();
        Reconnect();
        await CopyToLocal(sftp, source, localDirectory, progress, token);
        return state;
    }

    public async Task DownloadPreview(string source, string localDirectory,
        IProgress<TransferProgress> progress, CancellationToken token, long? maximumSize = null)
    {
        // Previews may become available while the regular SFTP connection is busy with another transfer.
        using var client = CreateSftpClient();
        await client.ConnectAsync(token);
        await CopyToLocal(client, source, localDirectory, progress, token, maximumSize);
    }

    public async Task<bool> HasRetentionForPreview(string path, CancellationToken token)
    {
        using var client = CreateSftpClient();
        await client.ConnectAsync(token);
        try
        {
            var file = await client.GetAsync(path, token);
            if (!file.IsRegularFile || file.IsSymbolicLink)
                throw new IOException("retention.json must be a regular file in this SURF folder.");
            if (file.Length > RetentionMetadata.MaximumFileSize)
                throw new IOException("retention.json is too large to preview.");
            return true;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    public async Task SaveRetention(string directory, string name, string originalSha, byte[] payload,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        if (!RetentionMetadata.IsFileName(name) || originalSha.Length != 64 || !originalSha.All(Uri.IsHexDigit) ||
            payload.Length > RetentionMetadata.MaximumFileSize ||
            (directory != Root && !directory.StartsWith(Root + "/", StringComparison.Ordinal)))
            throw new IOException("Invalid SURF retention file.");

        string path = Child(directory, name);
        string stageName = ".surfarchive-retention-" + Guid.NewGuid().ToString("N");
        string staging = Child(directory, stageName);
        string source = Quote("./" + stageName), destination = Quote("./" + name);
        using var client = CreateSftpClient();
        using var ssh = await OpenShellAsync(token);
        await client.ConnectAsync(token);
        string verifiedDirectory = $"cd -- {Quote(Root)} && root=$(pwd -P) && cd -- {Quote(directory)} && " +
            "case \"$(pwd -P)/\" in \"$root/\"*) ;; *) exit 4 ;; esac && ";
        await RunAsync(ssh, verifiedDirectory + $"test ! -L {destination} && test -f {destination}", token);

        try
        {
            using var input = new MemoryStream(payload, writable: false);
            await CopyUp(client, input, staging, name, progress, token);
            if (!ArchiveStatus.IsOnDisk(await ReadState(ssh, Quote(path), token)))
                throw new IOException("The retention file has returned to tape. Reopen it to restore the latest copy before saving.");
            string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string previous = originalSha.ToLowerInvariant();
            token.ThrowIfCancellationRequested();
            string result = await RunAsync(ssh, verifiedDirectory +
                $"test ! -L {source} && test -f {source} && test ! -L {destination} && test -f {destination} && " +
                $"staged=$(sha256sum < {source}) && current=$(sha256sum < {destination}) && " +
                $"if test \"${{staged%% *}}\" != '{expected}'; then echo checksum; " +
                $"elif test \"${{current%% *}}\" != '{previous}'; then echo changed; " +
                $"else chmod --reference={destination} -- {source} && mv -fT -- {source} {destination} && echo saved; fi",
                CancellationToken.None);
            if (result.Trim() == "changed")
                throw new IOException("The retention file changed on SURF. Reopen it before saving your changes.");
            if (result.Trim() != "saved")
                throw new IOException("SURF could not verify the updated retention file.");
        }
        catch (Exception error)
        {
            try
            {
                using var cleanup = OpenShell();
                Run(cleanup, verifiedDirectory + $"rm -f -- {source}");
            }
            catch (Exception cleanup)
            {
                throw new IOException($"{error.Message} Could not clean up the temporary file {staging}: {cleanup.Message}", error);
            }
            throw;
        }
    }

    private static async Task CopyToLocal(SftpClient client, string source, string localDirectory,
        IProgress<TransferProgress> progress, CancellationToken token, long? maximumSize = null, long? expectedSize = null)
    {
        token.ThrowIfCancellationRequested();
        string name = source[(source.LastIndexOf('/') + 1)..];
        FolderDownload.ValidateWindowsName(name);
        string target = Path.Combine(localDirectory, name);
        if (Path.Exists(target))
            throw new IOException($"A local file or folder named '{name}' already exists.");

        string staging = Path.Combine(localDirectory, ".surfarchive-download-" + Guid.NewGuid().ToString("N"));
        var attributes = await client.GetAsync(source, token);
        if (!attributes.IsRegularFile || attributes.IsSymbolicLink)
            throw new IOException($"The SURF source is no longer a regular file: {source}.");
        long size = attributes.Length;
        if (expectedSize is { } expected && size != expected)
            throw new IOException($"The SURF file changed size since the folder was scanned: {source}.");
        if (maximumSize is { } limit && size > limit)
            throw new IOException("The retention file is too large to edit.");
        var report = new Callback<DownloadFileProgressReport>(value =>
            progress.Report(new(name, checked((long)value.TotalBytesDownloaded), size)));
        var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await using (output)
            {
                progress.Report(new(name, 0, size));
                await client.DownloadFileAsync(source, output, report, token);
                if (output.Length != size)
                    throw new IOException("The downloaded file size differs from the source.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, target);
            progress.Report(new(name, size, size));
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public async Task VerifyUpload(string source, IProgress<TransferProgress> progress, Action<string> log,
        CancellationToken token)
    {
        using var input = OpenSource(source);
        string name = Path.GetFileName(source);
        log($"Source: {name}, {input.Length:N0} bytes.");
        string expected = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        input.Position = 0;
        Reconnect();
        using var ssh = OpenShell();
        Run(ssh, "command -v dals >/dev/null && command -v sha256sum >/dev/null && command -v rm >/dev/null && command -v rmdir >/dev/null");

        string directory = Child(Root, ".surfarchive-test-" + Guid.NewGuid().ToString("N"));
        string destination = Child(directory, name);
        string quotedFile = Quote(destination);
        string quotedDirectory = Quote(directory);
        string cleanupCommand = $"rm -f -- {quotedFile} && rmdir -- {quotedDirectory} && test ! -e {quotedDirectory} && test ! -L {quotedDirectory}";
        token.ThrowIfCancellationRequested();
        sftp.CreateDirectory(directory);
        try
        {
            log($"Temporary SURF path: {destination}");
            await CopyUp(sftp, input, destination, name, progress, token);
            token.ThrowIfCancellationRequested();
            log(Run(ssh, "dals -l -q " + quotedFile));
            string actual = Run(ssh, "sha256sum < " + quotedFile, timeoutSeconds: 90)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The SHA-256 on SURF differs from the source.");
            log("Matching SHA-256: " + actual);
        }
        finally
        {
            try
            {
                Run(ssh, cleanupCommand);
            }
            catch
            {
                // The first attempt may have succeeded even if its connection dropped.
                using var retry = OpenShell();
                Run(retry, $"if test -e {quotedDirectory} || test -L {quotedDirectory}; then {cleanupCommand}; fi");
            }
            log("Test file and temporary folder removed. Confirmed absent on SURF.");
        }
    }

    private static FileStream OpenSource(string source) => new(source, FileMode.Open, FileAccess.Read,
        FileShare.Read, bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose() => sftp.Dispose();

    public PackageDestination PackageTarget() => new(this);

    public void CheckArchiveDestinations(IReadOnlyList<ArchiveRequest> requests, CancellationToken token)
    {
        Reconnect();
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            if (sftp.Exists(request.RemotePath))
                throw new IOException($"The SURF destination already exists: {request.RemotePath}. Choose a different parent folder or name.");
        }
    }

    internal sealed class PackageDestination(ArchiveSession session) : IPackageDestination, IDisposable
    {
        private SftpClient? uploadClient;
        private SshClient? shell;
        private readonly HashSet<string> existing = new(StringComparer.Ordinal);
        private readonly PackageRetry retry = new(TimeProvider.System);
        private string directory = "";
        public string Root => session.Root;

        public async Task Prepare(string parent, string name, bool resume, Action<string> onRetry, CancellationToken token)
        {
            if ((parent != Root && !parent.StartsWith(Root + "/", StringComparison.Ordinal)) ||
                parent.Split('/').Any(part => part is "." or ".." || part.Any(char.IsControl)))
                throw new IOException("The package destination is outside this SURF archive.");

            directory = Child(parent, name);
            bool prepared = false;
            await retry.Run($"Preparing SURF folder {name}", async (_, ct) =>
            {
                using var validation = await session.OpenShellAsync(ct);
                await RunAsync(validation,
                    $"command -v sha256sum >/dev/null && cd -- {Quote(Root)} && archive_root=$(pwd -P) && cd -- {Quote(parent)} && " +
                    "case \"$(pwd -P)/\" in \"$archive_root/\"*) ;; *) exit 4 ;; esac", ct);
                var client = await ConnectUploadClient(ct);
                if (!prepared)
                {
                    bool exists = await client.ExistsAsync(directory, ct);
                    if (!resume && exists)
                        throw new IOException($"The SURF destination already exists: {directory}.");
                    if (!exists)
                        await client.CreateDirectoryAsync(directory, ct);
                    prepared = true;
                }
                await RunAsync(validation, "test -d " + Quote(directory) + " && test ! -L " + Quote(directory), ct);
                if (resume)
                {
                    existing.Clear();
                    await foreach (var entry in client.ListDirectoryAsync(directory, ct))
                        existing.Add(entry.Name);
                }
                return true;
            }, ResetUploadClient, onRetry, token);
        }

        public bool HasPart(string name) => existing.Contains(name);

        public async Task Upload(string localPath, ArchivePart part, IProgress<TransferProgress> progress,
            Action<string> onRetry, CancellationToken token)
        {
            string destination = Child(directory, part.Name);
            string staging = Child(directory, ".surfarchive-upload-" + Guid.NewGuid().ToString("N"));
            bool mayHaveStaging = false;
            bool publishAttempted = false;
            try
            {
                await retry.Run($"Upload of {part.Name}", async (attempt, ct) =>
                {
                    var client = await ConnectUploadClient(ct);
                    if (await client.ExistsAsync(destination, ct))
                    {
                        if (!publishAttempted)
                            throw new IOException($"A file or folder named '{part.Name}' already exists on SURF.");

                        // A rename may have succeeded just before the acknowledgement was lost.
                        string actual = await HashOnServer(part.Name, ct);
                        if (!part.Sha256.Equals(actual, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"The existing SURF file '{part.Name}' has a different SHA-256 checksum.");
                        if (mayHaveStaging)
                            await RemoveStaging(client, staging, ct);
                        progress.Report(new(part.Name, part.Size, part.Size));
                        return true;
                    }

                    if (attempt > 1 && mayHaveStaging)
                        await RemoveStaging(client, staging, ct);
                    using var input = OpenSource(localPath);
                    if (input.Length != part.Size)
                        throw new IOException($"The local size of {part.Name} changed before it was uploaded.");
                    mayHaveStaging = true;
                    await CopyUp(client, input, staging, part.Name, progress, ct);
                    ct.ThrowIfCancellationRequested();
                    publishAttempted = true;
                    await client.RenameFileAsync(staging, destination, ct);
                    return true;
                }, () =>
                {
                    ResetUploadClient();
                    ResetShell();
                }, onRetry, token);
            }
            catch (Exception error)
            {
                ResetUploadClient();
                if (mayHaveStaging)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await RemoveStaging(await ConnectUploadClient(cleanup.Token), staging, cleanup.Token);
                    }
                    catch (Exception cleanupError)
                    {
                        if (!token.IsCancellationRequested)
                            throw new IOException($"{error.Message} Could not remove temporary SURF file {staging}: {cleanupError.Message}", error);
                    }
                }
                token.ThrowIfCancellationRequested();
                throw;
            }
        }

        public Task<string> Hash(string name, Action<string> onRetry, CancellationToken token) =>
            retry.Run($"SHA-256 check of {name}", (_, ct) => HashOnServer(name, ct),
                ResetShell, onRetry, token);

        public Task StageForVerification(ArchivePart[] parts, Action<RestoreUpdate> report, Action<string> onRetry,
            CancellationToken token)
        {
            string[] paths = parts.Select(part => Child(directory, part.Name)).ToArray();
            return TapeRestore.WaitForFolder(paths,
                ct => retry.Run("Checking archive parts before SHA-256", async (_, attemptToken) =>
                    await ReadPathStatuses(await ConnectShell(attemptToken), paths, attemptToken), ResetShell, onRetry, ct),
                async (offline, ct) =>
                {
                    foreach (string command in PathCommands("daget -a", offline, ct))
                        await retry.Run("Requesting archive parts from tape", async (_, attemptToken) =>
                            await RunAsync(await ConnectShell(attemptToken), command, attemptToken, timeoutSeconds: 90),
                            ResetShell, onRetry, ct);
                }, report, ct => Task.Delay(RestorePolling.ManualInterval, ct), token);
        }

        private async Task<string> HashOnServer(string name, CancellationToken token)
        {
            string path = Quote(Child(directory, name));
            string value = await RunAsync(await ConnectShell(token), $"test -f {path} && test ! -L {path} && sha256sum < {path}",
                token, timeoutSeconds: 120);
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }

        private async Task<SshClient> ConnectShell(CancellationToken token)
        {
            if (shell is not { IsConnected: true })
            {
                ResetShell();
                shell = await session.OpenShellAsync(token);
            }
            return shell;
        }

        private static async Task RemoveStaging(SftpClient client, string path, CancellationToken token)
        {
            try { await client.DeleteFileAsync(path, token); }
            catch (SftpPathNotFoundException) { }
        }

        private async Task<SftpClient> ConnectUploadClient(CancellationToken token)
        {
            if (uploadClient is { IsConnected: true })
                return uploadClient;
            ResetUploadClient();
            uploadClient = session.CreateSftpClient();
            uploadClient.OperationTimeout = TimeSpan.FromMinutes(2);
            await uploadClient.ConnectAsync(token);
            return uploadClient;
        }

        private void ResetUploadClient()
        {
            uploadClient?.Dispose();
            uploadClient = null;
        }

        private void ResetShell()
        {
            shell?.Dispose();
            shell = null;
        }

        public void Dispose()
        {
            ResetUploadClient();
            ResetShell();
        }
    }
}

internal sealed class Callback<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
