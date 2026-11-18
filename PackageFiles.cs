using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SurfArchive;

internal record PackageEntry(string Path, string Name, bool IsDirectory, long Size, DateTime Modified);

internal sealed record PackagePlan(string Source, IReadOnlyList<PackageEntry> Entries, long Bytes, long Required,
    string StagingParent, bool UsesFallback)
{
    public static PackagePlan Create(string source, IReadOnlyList<ArchiveEntry> selected, CancellationToken token)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        PackageFiles.GetRegularAttributes(source);
        var entries = new List<PackageEntry>();
        foreach (var item in selected)
        {
            if (string.IsNullOrEmpty(item.Name) || item.Name is "." or ".." || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("A selected name is not a normal Windows file or folder name.");
            Add(Path.Combine(source, item.Name), item.Name, item.IsDirectory);
        }

        if (entries.Count == 0)
            throw new IOException("Select at least one file or folder.");
        long bytes = entries.Sum(item => item.Size);
        // Budget for incompressible input, archive headers, and package metadata.
        long required = checked(bytes + bytes / 256 + entries.Sum(item => 4096L + 12L * item.Name.Length) + (1L << 20));
        bool fallback = !IsArchiveDrive(source) && PackageFiles.AvailableBytes(source) < (ulong)required;
        string staging = fallback ? PackageFiles.FallbackFolder() : source;
        if (entries.Any(item => item.IsDirectory && PackageFiles.IsWithin(staging, item.Path)))
            throw new IOException("The staging location is inside a selected folder.");
        return new(source, entries, bytes, required, staging, fallback);

        void Add(string path, string name, bool? expectedDirectory)
        {
            token.ThrowIfCancellationRequested();
            string leaf = Path.GetFileName(path);
            if (leaf.EndsWith(' ') || leaf.EndsWith('.') || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException($"The name cannot be safely archived and removed on Windows: {path}");
            var info = PackageFiles.GetRegularAttributes(path);
            bool isDirectory = info.HasFlag(FileAttributes.Directory);
            if (expectedDirectory.HasValue && isDirectory != expectedDirectory.Value)
                throw new IOException($"'{path}' changed since selection.");
            if (isDirectory)
            {
                entries.Add(new(path, name + "/", true, 0, Directory.GetLastWriteTimeUtc(path)));
                foreach (string child in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                         { IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }))
                    Add(child, name + "/" + Path.GetFileName(child), null);
            }
            else
            {
                var file = new FileInfo(path);
                entries.Add(new(path, name, false, file.Length, file.LastWriteTimeUtc));
            }
        }
    }

    private static bool IsArchiveDrive(string path) => string.Equals(Path.GetPathRoot(path), @"L:\",
        StringComparison.OrdinalIgnoreCase);
}

internal static class PackageFiles
{
    public static string FallbackFolder()
    {
        string personal = Path.Combine(LocalFiles.ActiveArchive, "Personal Archive");
        string parent = Directory.Exists(personal) ? personal : LocalFiles.ActiveArchive;
        return Path.Combine(parent, ".surfarchive-temp");
    }

    public static bool IsWithin(string child, string parent)
    {
        string relative = Path.GetRelativePath(parent, child);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith("..\\", StringComparison.Ordinal);
    }

    public static ulong AvailableBytes(string directory)
    {
        if (!GetDiskFreeSpaceExW(directory, out ulong available, out _, out _))
            throw new IOException($"Could not check available space on {directory}.", new Win32Exception());
        return available;
    }

    public static FileAttributes GetRegularAttributes(string path)
    {
        return CheckRegularAttributes(File.GetAttributes(path), path);
    }

    private static FileAttributes CheckRegularAttributes(FileAttributes attributes, string path)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
            throw new IOException($"Reparse points and encrypted items are not supported: {path}");
        return attributes;
    }

    public static void CheckSource(PackageEntry entry, SafeFileHandle source)
    {
        if (CheckRegularAttributes(File.GetAttributes(source), entry.Path).HasFlag(FileAttributes.Directory)
            || File.GetLastWriteTimeUtc(source) != entry.Modified || RandomAccess.GetLength(source) != entry.Size)
            throw new IOException($"The original changed and was saved in place: {entry.Path}");
    }

    public static string Hash(Stream input, byte[] buffer, CancellationToken token, Action<int>? read = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int size;
        while ((size = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer.AsSpan(0, size));
            read?.Invoke(size);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void DeleteVerified(PackageEntry entry, string expectedHash, byte[] buffer,
        SafeFileHandle? verified, CancellationToken token)
    {
        // Mark the same handle used to identify the file, without following a replacement junction or symlink.
        using var handle = CreateFileW(LongPath(entry.Path), 0x80010000,
            (uint)FileShare.Read, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Could not lock the original for removal: {entry.Path}", new Win32Exception());
        CheckSource(entry, handle);
        EnsureDefaultDataStream(entry.Path);
        bool? same = verified is null ? null : CompareIdentity(verified, handle);
        if (same == false)
            throw new IOException($"The original was replaced and was saved in place: {entry.Path}");
        if (same == true)
        {
            MarkForRemoval(handle, entry.Path, token);
        }
        else
        {
            using var input = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            if (Hash(input, buffer, token) != expectedHash)
                throw new IOException($"The original changed and was saved in place: {entry.Path}");
            MarkForRemoval(handle, entry.Path, token);
        }
    }

    private static void MarkForRemoval(SafeFileHandle handle, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
            throw new IOException($"Could not remove the verified file: {path}", new Win32Exception());
    }

    private static bool? CompareIdentity(SafeFileHandle first, SafeFileHandle second)
    {
        if (ReadIdentity(first) is { } a && ReadIdentity(second) is { } b)
            return a == b;
        // Some SMB servers only return the older ID. Use it on NTFS, where the 64-bit ID identifies the file.
        if (ReadNtfsIdentity(first) is { } olderA && ReadNtfsIdentity(second) is { } olderB)
            return olderA == olderB;
        return null;
    }

    private static FileIdentity? ReadIdentity(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 18, out var info, 24) && (info.Low != 0 || info.High != 0) ? info : null;

    private static (uint Volume, ulong Index)? ReadNtfsIdentity(SafeFileHandle handle)
    {
        var fileSystem = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity)
            || !string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase)
            || !GetFileInformationByHandle(handle, out var info))
            return null;
        ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
        return info.Volume != 0 && index != 0 ? (info.Volume, index) : null;
    }

    internal static void EnsureDefaultDataStream(string path)
    {
        IntPtr handle = FindFirstStreamW(LongPath(path), 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            // File systems without named stream support return INVALID_FUNCTION or NOT_SUPPORTED.
            int error = Marshal.GetLastWin32Error();
            if (error is 1 or 38 or 50)
                return;
            throw new IOException($"Could not check data streams on {path}.", new Win32Exception(error));
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                    throw new IOException($"The original has a named data stream the archive would not preserve: {path}");
            }
            while (FindNextStreamW(handle, out data));
            int error = Marshal.GetLastWin32Error();
            if (error != 38)
                throw new IOException($"Could not check data streams on {path}.", new Win32Exception(error));
        }
        finally { FindClose(handle); }
    }

    private static string LongPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created;
        public System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
        public System.Runtime.InteropServices.ComTypes.FILETIME Modified;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdentity information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volume, uint volumeSize,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte information, uint size);
}
