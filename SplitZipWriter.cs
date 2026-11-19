using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace SurfArchive;

internal sealed record ZipEntryIndex(long DataOffset, long CompressedSize, uint Crc32, IReadOnlyList<int>? Blocks);

// Writes one ordinary ZIP to a non-seekable stream. VolumeWriter splits the byte stream
// into the numbered slices that 7-Zip opens as .zip.001, .zip.002, and so on.
internal sealed class SplitZipWriter(VolumeWriter stream, ZipBlockCodec codec, CancellationToken token) : IDisposable
{
    private const ushort Utf8 = 1 << 11;
    private const ushort DataDescriptor = 1 << 3;
    private const ushort Zip64Version = 45;
    private const ushort DeflateVersion = 20;
    private const ushort Stored = 0;
    private const ushort Deflate = 8;
    private const uint LocalHeader = 0x04034b50;
    private const uint DescriptorHeader = 0x08074b50;
    private const uint CentralHeader = 0x02014b50;
    private const uint CentralEnd = 0x06054b50;
    private const uint Zip64End = 0x06064b50;
    private const uint Zip64Locator = 0x07064b50;
    private readonly BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
    private readonly List<Entry> entries = [];
    private readonly byte[] buffer = new byte[1 << 20];

    public void Write(PackageEntry item, Stream? source, Action<int> report)
    {
        token.ThrowIfCancellationRequested();
        byte[] name = Encoding.UTF8.GetBytes(item.Name);
        ushort nameLength = checked((ushort)name.Length);
        bool hasData = item.Size > 0;
        ushort flags = hasData ? (ushort)(Utf8 | DataDescriptor) : Utf8;
        ushort method = hasData ? Deflate : Stored;
        // A non-seekable entry needs its descriptor width before writing any data.
        bool wideSizes = item.Size >= uint.MaxValue - uint.MaxValue / 128;
        long headerOffset = stream.BytesWritten;
        uint time = DosTime(item.Modified);
        ushort version = wideSizes || NeedsZip64(headerOffset) ? Zip64Version : DeflateVersion;

        writer.Write(LocalHeader);
        WriteCommon(version, flags, method, time);
        writer.Write(0U);
        writer.Write(wideSizes ? uint.MaxValue : 0U);
        writer.Write(wideSizes ? uint.MaxValue : 0U);
        writer.Write(nameLength);
        writer.Write((ushort)(wideSizes ? 20 : 0));
        writer.Write(name);
        if (wideSizes)
            WriteZip64Extra(true, false, 0, 0, 0);

        long dataOffset = stream.BytesWritten;
        var crc = new Crc32();
        List<int>? blocks = null;
        if (hasData)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (item.Size >= ZipBlockCodec.MinimumLength)
                blocks = codec.Compress(source, stream, item.Size, crc, report);
            else
                WriteSmallFile(source, item.Size, crc, report);
        }

        long compressed = stream.BytesWritten - dataOffset;
        uint checksum = crc.GetCurrentHashAsUInt32();
        if (hasData)
        {
            if (!wideSizes && NeedsZip64(compressed))
                throw new IOException($"The compressed ZIP entry exceeded its size allowance: {item.Name}");
            writer.Write(DescriptorHeader);
            writer.Write(checksum);
            WriteSize(compressed, wideSizes);
            WriteSize(item.Size, wideSizes);
        }
        entries.Add(new(name, version, flags, method, time, item.Size, item.IsDirectory, wideSizes, headerOffset,
            new(dataOffset, compressed, checksum, blocks)));
    }

    public IReadOnlyList<ZipEntryIndex> Finish()
    {
        long start = stream.BytesWritten;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            WriteCentralEntry(entry);
        }
        long size = stream.BytesWritten - start;
        bool zip64 = NeedsZip64(start) || NeedsZip64(size) || entries.Count >= ushort.MaxValue ||
            entries.Any(e => e.WideSizes);
        if (zip64)
        {
            long end = stream.BytesWritten;
            writer.Write(Zip64End);
            writer.Write(44UL);
            writer.Write(Zip64Version);
            writer.Write(Zip64Version);
            writer.Write(0U); // This stream uses a single logical ZIP disk.
            writer.Write(0U);
            writer.Write((ulong)entries.Count);
            writer.Write((ulong)entries.Count);
            writer.Write((ulong)size);
            writer.Write((ulong)start);
            writer.Write(Zip64Locator);
            writer.Write(0U);
            writer.Write((ulong)end);
            writer.Write(1U);
        }

        writer.Write(CentralEnd);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(zip64 ? ushort.MaxValue : (ushort)entries.Count);
        writer.Write(zip64 ? ushort.MaxValue : (ushort)entries.Count);
        writer.Write(zip64 ? uint.MaxValue : (uint)size);
        writer.Write(zip64 ? uint.MaxValue : (uint)start);
        writer.Write((ushort)0);
        return entries.Select(e => e.Index).ToArray();
    }

    private void WriteSmallFile(Stream source, long remaining, Crc32 crc, Action<int> report)
    {
        using var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            var data = buffer.AsSpan(0, count);
            source.ReadExactly(data);
            crc.Append(data);
            deflate.Write(data);
            report(count);
            remaining -= count;
        }
    }

    private void WriteCentralEntry(Entry entry)
    {
        bool wideOffset = NeedsZip64(entry.HeaderOffset);
        ushort extraLength = (ushort)((entry.WideSizes ? 16 : 0) + (wideOffset ? 8 : 0));
        writer.Write(CentralHeader);
        writer.Write(entry.Version); // DOS host, with attributes in the low byte.
        WriteCommon(entry.Version, entry.Flags, entry.Method, entry.Time);
        writer.Write(entry.Index.Crc32);
        writer.Write(entry.WideSizes ? uint.MaxValue : (uint)entry.Index.CompressedSize);
        writer.Write(entry.WideSizes ? uint.MaxValue : (uint)entry.Size);
        writer.Write((ushort)entry.Name.Length);
        writer.Write((ushort)(extraLength > 0 ? extraLength + 4 : 0));
        writer.Write((ushort)0); // Comment length.
        writer.Write((ushort)0); // Starting disk.
        writer.Write((ushort)0); // Internal attributes.
        writer.Write(entry.IsDirectory ? (uint)FileAttributes.Directory : 0U);
        writer.Write(wideOffset ? uint.MaxValue : (uint)entry.HeaderOffset);
        writer.Write(entry.Name);
        if (extraLength > 0)
            WriteZip64Extra(entry.WideSizes, wideOffset, entry.Size, entry.Index.CompressedSize, entry.HeaderOffset);
    }

    private void WriteCommon(ushort version, ushort flags, ushort method, uint time)
    {
        writer.Write(version);
        writer.Write(flags);
        writer.Write(method);
        writer.Write(time);
    }

    private void WriteZip64Extra(bool sizes, bool offset, long size, long compressed, long headerOffset)
    {
        writer.Write((ushort)1);
        writer.Write((ushort)((sizes ? 16 : 0) + (offset ? 8 : 0)));
        if (sizes)
        {
            writer.Write((ulong)size);
            writer.Write((ulong)compressed);
        }
        if (offset)
            writer.Write((ulong)headerOffset);
    }

    private void WriteSize(long value, bool wide)
    {
        if (wide)
            writer.Write((ulong)value);
        else
            writer.Write((uint)value);
    }

    private static bool NeedsZip64(long value) => value >= uint.MaxValue;

    private static uint DosTime(DateTime modified)
    {
        var local = new DateTimeOffset(modified).ToLocalTime();
        if (local.Year is < 1980 or > 2107)
            local = DateTimeOffset.Now;
        int date = (local.Year - 1980) << 9 | local.Month << 5 | local.Day;
        int time = local.Hour << 11 | local.Minute << 5 | local.Second / 2;
        return (uint)(date << 16 | time);
    }

    public void Dispose() => writer.Dispose();

    private sealed record Entry(byte[] Name, ushort Version, ushort Flags, ushort Method, uint Time, long Size,
        bool IsDirectory, bool WideSizes, long HeaderOffset, ZipEntryIndex Index);
}
