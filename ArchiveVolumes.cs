using System.Security.Cryptography;

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
    private string name = "";
    private long length;
    public override bool CanRead => false;
    public override bool CanWrite => true;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (stream is null)
            {
                name = ArchivePackage.VolumeName(archiveName, parts.Count + 1);
                stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            }
            int count = (int)Math.Min(data.Length, maximum - length);
            var chunk = data[..count];
            stream.Write(chunk);
            hash.AppendData(chunk);
            length += count;
            data = data[count..];
            if (length == maximum)
                CloseVolume();
        }
    }

    public ArchivePart[] Finish()
    {
        CloseVolume();
        return parts.ToArray();
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

// ZipArchive needs to seek to the central directory at the end of the logical ZIP.
internal sealed class ZipVolumeReader(string folder, ArchivePart[] parts, CancellationToken token) : SequentialStream
{
    private readonly long[] offsets = PartOffsets(parts);
    private FileStream? current;
    private int currentIndex = -1;
    private long position;

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => true;
    public override long Length => offsets[^1];
    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = checked(offset + (origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        }));
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        if (position >= Length || buffer.IsEmpty)
            return 0;
        int index = Array.BinarySearch(offsets, position);
        if (index < 0)
            index = ~index - 1;
        if (currentIndex != index)
        {
            current?.Dispose();
            current = File.OpenRead(Path.Combine(folder, parts[index].Name));
            if (current.Length != parts[index].Size)
                throw new IOException($"The size of {parts[index].Name} changed.");
            currentIndex = index;
        }
        current!.Position = position - offsets[index];
        int count = current.Read(buffer[..(int)Math.Min(buffer.Length, offsets[index + 1] - position)]);
        if (count == 0)
            throw new EndOfStreamException($"Could not read ZIP volume {parts[index].Name}.");
        position += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            current?.Dispose();
        base.Dispose(disposing);
    }

    private static long[] PartOffsets(ArchivePart[] volumes)
    {
        var result = new long[volumes.Length + 1];
        for (int i = 0; i < volumes.Length; i++)
            result[i + 1] = checked(result[i] + volumes[i].Size);
        return result;
    }
}
