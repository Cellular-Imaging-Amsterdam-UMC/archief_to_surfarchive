using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SurfArchive;

internal record ArchiveEntry(string Name, bool IsDirectory, long Size, DateTime Modified);
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
            throw new ArgumentException("Ongeldige SURF-gebruikersnaam.");
        this.credentials = credentials;
        Root = "/archive/" + credentials.Username;
        sftp = Configure(new SftpClient(Connection()));
        sftp.OperationTimeout = TimeSpan.FromSeconds(45);
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
            if (entry.Name is "." or ".." || entry.IsSymbolicLink || (!entry.IsDirectory && !entry.IsRegularFile))
                continue;
            entries.Add(new(entry.Name, entry.IsDirectory, entry.Length, entry.LastWriteTime));
        }
        return entries;
    }

    public static string Child(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Any(char.IsControl))
            throw new ArgumentException("Ongeldige bestands- of mapnaam.");
        return parent + "/" + name;
    }

    public void MakeDirectory(string directory)
    {
        Reconnect();
        sftp.CreateDirectory(directory);
    }

    public string Status(string file)
    {
        using var ssh = OpenShell();
        return Run(ssh, "dals -l -q " + Quote(file));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string Run(SshClient ssh, string commandText, int timeoutSeconds = 30)
    {
        using var command = ssh.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        string output = command.Execute().Trim();
        if (command.ExitStatus != 0)
            throw new IOException($"SURF gaf een fout (exit {command.ExitStatus}): {command.Error.Trim()}");
        return output;
    }

    private async Task CopyUp(FileStream input, string destination, string name,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        long size = input.Length;
        var report = new Callback<UploadFileProgressReport>(value =>
            progress.Report(new(name, checked((long)value.TotalBytesUploaded), size)));
        await sftp.UploadFileAsync(input, destination, canOverride: false, report, token);
        if (sftp.GetAttributes(destination).Size != size)
            throw new IOException("De bestandsgrootte op SURF wijkt af van de bron.");
        progress.Report(new(name, size, size));
    }

    public async Task Upload(string source, string directory, IProgress<TransferProgress> progress, CancellationToken token)
    {
        Reconnect();
        string name = Path.GetFileName(source);
        string destination = Child(directory, name);
        if (sftp.Exists(destination))
            throw new IOException($"Op SURF bestaat al een bestand of map met de naam '{name}'.");
        string staging = Child(directory, ".surfarchive-upload-" + Guid.NewGuid().ToString("N"));
        using var input = OpenSource(source);

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
                throw new IOException($"Controleer tijdelijk SURF-bestand {staging}: {cleanup.Message}", failure);
            }
            throw;
        }
    }

    public async Task Download(string source, string localDirectory,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        Reconnect();
        string name = source[(source.LastIndexOf('/') + 1)..];
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException($"Windows ondersteunt de bestandsnaam '{name}' niet.");
        string target = Path.Combine(localDirectory, name);
        if (Path.Exists(target))
            throw new IOException($"Lokaal bestaat al een bestand of map met de naam '{name}'.");
        string staging = Path.Combine(localDirectory, ".surfarchive-download-" + Guid.NewGuid().ToString("N"));
        long size = sftp.GetAttributes(source).Size;
        var report = new Callback<DownloadFileProgressReport>(value =>
            progress.Report(new(name, checked((long)value.TotalBytesDownloaded), size)));
        var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await using (output)
            {
                await sftp.DownloadFileAsync(source, output, report, token);
                if (output.Length != size)
                    throw new IOException("De gedownloade bestandsgrootte wijkt af van de bron.");
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
        log($"Bron: {name}, {input.Length:N0} bytes.");
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
            log($"Tijdelijk SURF-pad: {destination}");
            await CopyUp(input, destination, name, progress, token);
            token.ThrowIfCancellationRequested();
            log(Run(ssh, "dals -l -q " + quotedFile));
            string actual = Run(ssh, "sha256sum < " + quotedFile, timeoutSeconds: 90)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new IOException("De SHA-256 op SURF wijkt af van de bron.");
            log("SHA-256 gelijk: " + actual);
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
            log("Testbestand en tijdelijke map verwijderd. Afwezigheid bevestigd op SURF.");
        }
    }

    private static FileStream OpenSource(string source) => new(source, FileMode.Open, FileAccess.Read,
        FileShare.Read, bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose() => sftp.Dispose();
}

internal sealed class Callback<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
