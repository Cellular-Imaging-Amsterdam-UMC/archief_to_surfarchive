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

        var session = SignInAtStartup();
        if (session is null)
            return 0;

        if (args is ["--proef" or "--probe", var source, var logPath])
        {
            using (session)
                return RunProbe(session, source, logPath);
        }
        Application.Run(new MainWindow(session));
        return 0;
    }

    private static ArchiveSession? SignInAtStartup()
    {
        using var splash = new StartupSplash();
        ArchiveSession? session = null;
        splash.Shown += async (_, _) =>
        {
            try
            {
                // Give a warm launch enough time to paint the splash without leaving it over the sign-in window.
                var minimumDisplay = Task.Delay(350);
                using var login = new LoginDialog();
                await minimumDisplay;
                if (splash.IsDisposed)
                    return;
                login.Shown += (_, _) => splash.Hide();
                if (login.ShowDialog() == DialogResult.OK)
                    session = login.TakeSession();
            }
            finally
            {
                if (!splash.IsDisposed)
                    splash.Close();
            }
        };
        Application.Run(splash);
        return session;
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
