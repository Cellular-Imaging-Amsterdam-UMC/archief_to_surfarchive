using System.Globalization;

namespace SurfArchive;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
        ApplicationConfiguration.Initialize();

        using var login = new LoginDialog();
        if (login.ShowDialog() != DialogResult.OK)
            return 0;

        var session = login.TakeSession();
        if (args is ["--proef" or "--probe", var source, var logPath])
        {
            using (session)
                return RunProbe(session, source, logPath);
        }
        Application.Run(new MainWindow(session));
        return 0;
    }

    private static int RunProbe(ArchiveSession archive, string source, string logPath)
    {
        using var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Log(string line) => writer.WriteLine($"{DateTimeOffset.Now:O} {line}");

        try
        {
            Log($"Connected to {ArchiveSession.Host}. Personal archive: {archive.Root}");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Callback<TransferProgress>(value =>
            {
                if (timer.ElapsedMilliseconds < 1000 && value.Bytes != value.Total)
                    return;
                Log($"Uploaded {value.Bytes:N0} / {value.Total:N0} bytes.");
                timer.Restart();
            });
            archive.VerifyUpload(source, progress, Log, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            Log("Error: " + error.Message);
            return 1;
        }
    }
}
