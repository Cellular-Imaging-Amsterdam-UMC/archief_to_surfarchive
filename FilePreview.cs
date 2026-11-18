using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SurfArchive;

internal static class FilePreview
{
    private static readonly string TempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SurfDataArchiveTransfer-Previews"));

    public static bool IsSupported(string name) => Path.GetExtension(name).ToLowerInvariant() is ".html" or ".htm" or ".txt";

    public static void Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The file could not be opened.", fullPath);
        using var process = Process.Start(OpenCommand(fullPath));
    }

    internal static ProcessStartInfo OpenCommand(string fullPath)
    {
        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension == ".txt")
        {
            var notepad = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            notepad.ArgumentList.Add(fullPath);
            return notepad;
        }
        if (extension is not (".html" or ".htm"))
            throw new InvalidOperationException("Only HTML and text files can be opened.");

        string? browser = BrowserExecutable("https") ?? BrowserExecutable("http");
        if (browser is null)
            return new ProcessStartInfo(fullPath) { UseShellExecute = true };

        var command = new ProcessStartInfo(browser);
        string normalPath = fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + fullPath[8..]
            : fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath[4..] : fullPath;
        // Build the URI from path segments so a literal %20 stays part of the filename.
        string[] segments = normalPath.Split('\\', '/');
        string uri = normalPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? "file://" + segments[2] + "/" + string.Join('/', segments.Skip(3).Select(Uri.EscapeDataString))
            : "file:///" + segments[0] + "/" + string.Join('/', segments.Skip(1).Select(Uri.EscapeDataString));
        command.ArgumentList.Add(uri);
        return command;
    }

    private static string? BrowserExecutable(string protocol)
    {
        const uint isProtocol = 0x1000;
        const uint executable = 2;
        uint size = 0;
        AssocQueryStringW(isProtocol, executable, protocol, "open", null, ref size);
        if (size == 0 || size > 32_768)
            return null;
        var path = new StringBuilder((int)size);
        return AssocQueryStringW(isProtocol, executable, protocol, "open", path, ref size) == 0
            && Path.IsPathFullyQualified(path.ToString()) && File.Exists(path.ToString())
            ? path.ToString() : null;
    }

    public static string CreateTemporaryFolder()
    {
        Directory.CreateDirectory(TempRoot);
        if (!IsOrdinaryDirectory(TempRoot))
            throw new IOException("The temporary preview location is not a normal folder.");
        return Directory.CreateDirectory(Path.Combine(TempRoot, Guid.NewGuid().ToString("N"))).FullName;
    }

    public static void PruneTemporaryFolders()
    {
        try
        {
            if (!Directory.Exists(TempRoot) || !IsOrdinaryDirectory(TempRoot))
                return;
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (string directory in Directory.EnumerateDirectories(TempRoot))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    TryRemoveTemporaryFolder(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An open or inaccessible preview must not prevent startup.
        }
    }

    public static void TryRemoveTemporaryFolder(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(directory), TempRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                || !IsOrdinaryDirectory(TempRoot) || !IsOrdinaryDirectory(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                const string partialPrefix = ".surfarchive-download-";
                string name = Path.GetFileName(file);
                bool partial = name.StartsWith(partialPrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(name[partialPrefix.Length..], "N", out _);
                if ((IsSupported(name) || partial) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    File.Delete(file);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leave a preview in place if a viewer is still using it.
        }
    }

    private static bool IsOrdinaryDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint str, string association, string? verb,
        StringBuilder? output, ref uint characters);
}
