using System.Globalization;

namespace SurfArchive;

internal sealed class SessionLog : IDisposable
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfArchive", "Logs");

    private readonly object gate = new();
    private StreamWriter? writer;
    private string? error;

    public SessionLog(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        try
        {
            Directory.CreateDirectory(Folder);
            string date = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
            var stream = new FileStream(Path.Combine(Folder, $"SurfDataArchiveTransfer_{date}_{Environment.ProcessId}.log"),
                FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream);
            writer.AutoFlush = true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = failure.Message;
            CloseWriter();
        }
    }

    public string Folder { get; }

    public string? Error
    {
        get { lock (gate) return error; }
    }

    public void Write(string message)
    {
        lock (gate)
        {
            try { writer?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}"); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                error = failure.Message;
                CloseWriter();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
            CloseWriter();
    }

    private void CloseWriter()
    {
        try { writer?.Dispose(); }
        catch (IOException failure) { error ??= failure.Message; }
        finally { writer = null; }
    }
}
