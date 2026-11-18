using System.Security.Cryptography;
using System.IO.Compression;

namespace SurfArchive;

internal record ArchivePart(string Name, long Size, string Sha256);

internal abstract class SequentialStream : Stream
{
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}

internal sealed class VolumeWriter(string folder, string archiveName, long maximum,
    Action<ArchivePart> sealedVolume) : SequentialStream
{
    private FileStream? stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<ArchivePart> parts = [];
    private readonly byte[] block = new byte[(int)Math.Min(maximum / 4, 4 * 1024 * 1024)];
    private readonly MemoryStream compressed = new();
    private int buffered;
    private string name = "";
    private long length;
    public override bool CanRead => false;
    public override bool CanWrite => true;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int count = Math.Min(data.Length, block.Length - buffered);
            data[..count].CopyTo(block.AsSpan(buffered));
            buffered += count;
            data = data[count..];
            if (buffered == block.Length)
                WriteGzipMember();
        }
    }

    public ArchivePart[] Finish()
    {
        WriteGzipMember();
        CloseVolume();
        return parts.ToArray();
    }

    private void WriteGzipMember()
    {
        if (buffered == 0)
            return;
        compressed.SetLength(0);
        compressed.Position = 0;
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(block.AsSpan(0, buffered));
        buffered = 0;
        int size = checked((int)compressed.Length);
        if (size > maximum)
            throw new IOException("A compressed block exceeds the archive part size.");

        // End a volume before the next complete gzip member would exceed its limit.
        if (stream is not null && length + size > maximum)
            CloseVolume();
        if (stream is null)
        {
            name = ArchivePackage.VolumeName(archiveName, parts.Count + 1);
            stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        }
        var member = compressed.GetBuffer().AsSpan(0, size);
        stream.Write(member);
        hash.AppendData(member);
        length += size;
    }

    private void CloseVolume()
    {
        if (stream is null)
            return;
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        stream = null;
        var part = new ArchivePart(name, length, Convert.ToHexString(hash.GetHashAndReset()));
        parts.Add(part);
        length = 0;
        sealedVolume(part);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream?.Dispose();
            hash.Dispose();
            compressed.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class VolumeReader(string folder, ArchivePart[] parts, Action<int> read, CancellationToken token) : SequentialStream
{
    private FileStream? stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int index;
    private long remaining;
    public override bool CanRead => true;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || index == parts.Length)
            return 0;
        if (stream is null)
        {
            stream = new FileStream(Path.Combine(folder, parts[index].Name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            remaining = parts[index].Size;
            if (stream.Length != remaining)
                throw new IOException($"The size of {parts[index].Name} changed.");
        }

        int size = stream.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        if (size == 0)
            throw new EndOfStreamException($"Could not read the full archive part {parts[index].Name}.");
        hash.AppendData(buffer[..size]);
        remaining -= size;
        read(size);
        if (remaining == 0)
        {
            string digest = Convert.ToHexString(hash.GetHashAndReset());
            if (!digest.Equals(parts[index].Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Local SHA-256 mismatch for {parts[index].Name}.");
            stream.Dispose();
            stream = null;
            index++;
        }
        return size;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream?.Dispose();
            hash.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class HashingFileStream(FileStream source, Action<int> read, CancellationToken token) : SequentialStream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long ReadBytes { get; private set; }
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => throw new NotSupportedException(); }
    public override bool CanSeek => true; // TarWriter needs the length up front. Reads below remain sequential.
    public override bool CanRead => true;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        int size = source.Read(buffer);
        ReadBytes += size;
        hash.AppendData(buffer[..size]);
        read(size);
        return size;
    }

    public string Finish() => Convert.ToHexString(hash.GetHashAndReset());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            hash.Dispose();
        base.Dispose(disposing);
    }
}
