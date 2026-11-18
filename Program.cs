namespace SurfArchive;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--proef", var source, var logPath])
            return RunProbe(source, logPath);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
        return 0;
    }

    private static int RunProbe(string source, string logPath)
    {
        using var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Log(string line) => writer.WriteLine($"{DateTimeOffset.Now:O} {line}");

        try
        {
            var account = Credentials.Load() ?? throw new InvalidOperationException("Verbind eerst vanuit de app en bewaar het account lokaal.");
            using var archive = new ArchiveSession(account);
            Log($"SFTP-login gelukt op {ArchiveSession.Host}. Persoonlijk archief: {archive.Root}");
            var entries = archive.List(archive.Root).GetAwaiter().GetResult();
            Log($"Persoonlijk archief geopend: {entries.Count} zichtbare bestanden en mappen.");
            var last = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Callback<TransferProgress>(value =>
            {
                if (last.ElapsedMilliseconds >= 1000 || value.Bytes == value.Total)
                {
                    Log($"Upload {value.Bytes:N0} / {value.Total:N0} bytes.");
                    last.Restart();
                }
            });
            archive.VerifyUpload(source, progress, Log, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            Log("Fout: " + exception.Message);
            return 1;
        }
    }
}
