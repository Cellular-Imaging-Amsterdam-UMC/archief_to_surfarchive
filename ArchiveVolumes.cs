using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
    public long BytesWritten { get; private set; }
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
            BytesWritten += count;
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

internal sealed class HashingFileStream(SafeFileHandle source, Action<int>? read, CancellationToken token) : SequentialStream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long ReadBytes { get; private set; }
    public override bool CanRead => true;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        int size = RandomAccess.Read(source, buffer, ReadBytes);
        ReadBytes += size;
        hash.AppendData(buffer[..size]);
        read?.Invoke(size);
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
internal sealed class ZipVolumeReader(ArchivePart[] parts, Func<string, SafeFileHandle> open, CancellationToken token) : SequentialStream
{
    private readonly long[] offsets = PartOffsets(parts);
    private readonly byte[] window = new byte[1 << 20];
    private long windowStart;
    private int windowLength;
    private int windowPart;
    private long position;
    private IncrementalHash? checksum;
    private Action<int>? verifiedBytes;
    private long verifiedThrough;

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
        if (checksum is not null)
            VerifyThrough(position);
        if (!Contains(position))
            ReadWindow(position);
        HashWindow();

        int offset = (int)(position - windowStart);
        int count = Math.Min(buffer.Length, windowLength - offset);
        window.AsSpan(offset, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public void StartVerification(Action<int> report)
    {
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            if (RandomAccess.GetLength(open(part.Name)) != part.Size)
                throw new IOException($"The size of {part.Name} changed.");
        }
        checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        verifiedBytes = report;
    }

    public void CompleteVerification()
    {
        if (checksum is null)
            throw new InvalidOperationException("ZIP verification has not started.");
        VerifyThrough(Length);
    }

    private bool Contains(long offset) => offset >= windowStart && offset < windowStart + windowLength;

    private void ReadWindow(long offset)
    {
        int index = Array.BinarySearch(offsets, offset);
        if (index < 0)
            index = ~index - 1;
        long block = (offset - offsets[index]) / window.Length * window.Length;
        long start = offsets[index] + block;
        int size = (int)Math.Min(window.Length, offsets[index + 1] - start);
        var handle = open(parts[index].Name);
        int read = 0;
        while (read < size)
        {
            token.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(handle, window.AsSpan(read, size - read), block + read);
            if (count == 0)
                throw new EndOfStreamException($"Could not read ZIP volume {parts[index].Name}.");
            read += count;
        }
        windowStart = start;
        windowLength = size;
        windowPart = index;
    }

    private void VerifyThrough(long end)
    {
        // ZIP reads may skip headers or descriptors. Hash those gaps and the directory at the end too.
        while (verifiedThrough < end)
        {
            token.ThrowIfCancellationRequested();
            if (!Contains(verifiedThrough))
                ReadWindow(verifiedThrough);
            HashWindow();
        }
    }

    private void HashWindow()
    {
        if (checksum is null || !Contains(verifiedThrough))
            return;
        int offset = (int)(verifiedThrough - windowStart);
        int count = windowLength - offset;
        checksum.AppendData(window.AsSpan(offset, count));
        verifiedThrough += count;
        if (verifiedThrough == offsets[windowPart + 1] &&
            !Convert.ToHexString(checksum.GetHashAndReset()).Equals(parts[windowPart].Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Local SHA-256 mismatch for {parts[windowPart].Name}.");
        verifiedBytes!(count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            checksum?.Dispose();
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
