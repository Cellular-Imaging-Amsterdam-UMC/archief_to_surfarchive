using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SurfArchive;

internal record ArchiveEntry(string Name, bool IsDirectory, long Size, DateTime Modified,
    ArchiveState? Status = null);
internal record TransferProgress(string Name, long Bytes, long Total);

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
        var entries = new List<ArchiveEntry>();
        await foreach (var entry in sftp.ListDirectoryAsync(directory, CancellationToken.None))
        {
            if (entry.Name is "." or ".." || entry.Name.Any(char.IsControl) || entry.IsSymbolicLink
                || (!entry.IsDirectory && !entry.IsRegularFile))
                continue;
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
        var files = entries.Where(entry => !entry.IsDirectory).ToArray();
        var result = new Dictionary<string, ArchiveState>(StringComparer.Ordinal);
        if (files.Length == 0)
            return result;

        using var ssh = await OpenShellAsync(token);
        const string command = "daattr -a path,state -d ,";
        var batch = new StringBuilder(command);
        int bytes = 0;
        foreach (var entry in files)
        {
            string arg = " " + Quote(Child(directory, entry.Name));
            int size = Encoding.UTF8.GetByteCount(arg);
            if (bytes > 0 && bytes + size > 32_000)
            {
                await ReadBatch();
                batch.Clear().Append(command);
                bytes = 0;
            }
            batch.Append(arg);
            bytes += size;
        }
        await ReadBatch();
        return result;

        async Task ReadBatch()
        {
            string output = await RunAsync(ssh, batch.ToString(), token, timeoutSeconds: 60);
            foreach (var row in ArchiveStatus.ParseRows(output))
            {
                if (row.Key.StartsWith(directory + "/", StringComparison.Ordinal))
                    result[row.Key[(directory.Length + 1)..]] = row.Value;
            }
        }
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

    private async Task CopyUp(FileStream input, string destination, string name,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        long size = input.Length;
        var report = new Callback<UploadFileProgressReport>(value =>
            progress.Report(new(name, checked((long)value.TotalBytesUploaded), size)));
        progress.Report(new(name, 0, size));
        await sftp.UploadFileAsync(input, destination, canOverride: false, report, token);
        if (sftp.GetAttributes(destination).Size != size)
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
            await CopyUp(input, staging, name, progress, token);
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
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        // Previews may become available while the regular SFTP connection is busy with another transfer.
        using var client = CreateSftpClient();
        await client.ConnectAsync(token);
        await CopyToLocal(client, source, localDirectory, progress, token);
    }

    private static async Task CopyToLocal(SftpClient client, string source, string localDirectory,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = source[(source.LastIndexOf('/') + 1)..];
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException($"Windows does not support the filename '{name}'.");
        string target = Path.Combine(localDirectory, name);
        if (Path.Exists(target))
            throw new IOException($"A local file or folder named '{name}' already exists.");

        string staging = Path.Combine(localDirectory, ".surfarchive-download-" + Guid.NewGuid().ToString("N"));
        long size = client.GetAttributes(source).Size;
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
            await CopyUp(input, destination, name, progress, token);
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
        private SshClient? shell;
        private string directory = "";
        public string Root => session.Root;

        public async Task Prepare(string parent, string name, bool resume, CancellationToken token)
        {
            if ((parent != Root && !parent.StartsWith(Root + "/", StringComparison.Ordinal)) ||
                parent.Split('/').Any(part => part is "." or ".." || part.Any(char.IsControl)))
                throw new IOException("The package destination is outside this SURF archive.");

            directory = Child(parent, name);
            shell = await session.OpenShellAsync(token);
            await RunAsync(shell,
                $"command -v sha256sum >/dev/null && cd -- {Quote(Root)} && archive_root=$(pwd -P) && cd -- {Quote(parent)} && " +
                "case \"$(pwd -P)/\" in \"$archive_root/\"*) ;; *) exit 4 ;; esac", token);
            session.Reconnect();
            token.ThrowIfCancellationRequested();
            if (!resume || !session.sftp.Exists(directory))
                session.sftp.CreateDirectory(directory);
            await RunAsync(shell, "test -d " + Quote(directory) + " && test ! -L " + Quote(directory), token);
        }

        public bool HasPart(string name) => session.sftp.Exists(Child(directory, name));

        public Task Upload(string localPath, IProgress<TransferProgress> progress, CancellationToken token) =>
            session.Upload(localPath, directory, progress, token);

        public async Task<string> Hash(string name, CancellationToken token)
        {
            string path = Quote(Child(directory, name));
            string value = await RunAsync(shell!, $"test -f {path} && test ! -L {path} && sha256sum < {path}",
                token, timeoutSeconds: 120);
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }

        public void Dispose() => shell?.Dispose();
    }
}

internal sealed class Callback<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
