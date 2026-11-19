using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace SurfArchive;

internal sealed class ZipBlockCodec(CancellationToken token) : IDisposable
{
    internal const int BlockSize = 4 << 20;
    internal const int MinimumLength = 2 * BlockSize;
    private const int Workers = 8;
    private const int MaxEncodedBlock = BlockSize + BlockSize / 128 + 64;
    private static ReadOnlySpan<byte> EndBlock => [3, 0];
    private static ReadOnlySpan<byte> SyncTail => [0, 0, 255, 255];
    private readonly Block?[] blocks = new Block[Workers];

    public List<int> Compress(Stream source, Stream destination, long length, Crc32 crc, Action<int> report)
    {
        var encodedSizes = new List<int>();
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            int count = 0;
            while (count < Workers && length > 0)
            {
                var block = GetBlock(count++);
                block.PlainSize = (int)Math.Min(BlockSize, length);
                var data = block.Plain.AsSpan(0, block.PlainSize);
                source.ReadExactly(data);
                crc.Append(data);
                length -= block.PlainSize;
            }

            InParallel(count, i =>
            {
                var block = GetBlock(i);
                block.Encoded.SetLength(0);
                using var deflate = new DeflateStream(block.Encoded, CompressionLevel.Optimal, leaveOpen: true);
                deflate.Write(block.Plain.AsSpan(0, block.PlainSize));
                deflate.Flush();
                block.EncodedSize = (int)block.Encoded.Length;
                // Flush aligns a non-final block to a byte boundary so independent streams can be joined.
                // Ignore the terminal bytes written by Dispose; the ZIP entry ends with one shared marker.
                ValidateBlock(block);
            });

            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var block = GetBlock(i);
                destination.Write(block.Encoded.GetBuffer().AsSpan(0, block.EncodedSize));
                encodedSizes.Add(block.EncodedSize);
                report(block.PlainSize);
            }
        }
        destination.Write(EndBlock);
        return encodedSizes;
    }

    public string Verify(Stream source, long length, IReadOnlyList<int> encodedSizes, Action<int> report)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int start = 0; start < encodedSizes.Count;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(Workers, encodedSizes.Count - start);
            for (int i = 0; i < count; i++)
            {
                var block = GetBlock(i);
                block.PlainSize = (int)Math.Min(BlockSize, length);
                block.EncodedSize = encodedSizes[start + i];
                if (block.PlainSize == 0 || block.EncodedSize is < 5 or > MaxEncodedBlock)
                    throw new InvalidDataException("The ZIP has an invalid DEFLATE block size.");
                source.ReadExactly(block.Encoded.GetBuffer().AsSpan(0, block.EncodedSize));
                ValidateBlock(block);
                length -= block.PlainSize;
            }

            InParallel(count, i =>
            {
                var block = GetBlock(i);
                var bytes = block.Encoded.GetBuffer();
                EndBlock.CopyTo(bytes.AsSpan(block.EncodedSize));
                using var input = new MemoryStream(bytes, 0, block.EncodedSize + EndBlock.Length, writable: false);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                deflate.ReadExactly(block.Plain.AsSpan(0, block.PlainSize));
                if (deflate.ReadByte() != -1)
                    throw new InvalidDataException("A ZIP block expanded past its expected size.");
            });

            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var block = GetBlock(i);
                hash.AppendData(block.Plain.AsSpan(0, block.PlainSize));
                report(block.PlainSize);
            }
            start += count;
        }

        Span<byte> end = stackalloc byte[2];
        source.ReadExactly(end);
        if (length != 0 || !end.SequenceEqual(EndBlock))
            throw new InvalidDataException("The ZIP entry has an invalid DEFLATE ending.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private Block GetBlock(int index) => blocks[index] ??= new();

    private void InParallel(int count, Action<int> action)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, action);
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerExceptions[0]);
        }
    }

    private static void ValidateBlock(Block block)
    {
        if (block.EncodedSize is < 5 or > MaxEncodedBlock ||
            !block.Encoded.GetBuffer().AsSpan(block.EncodedSize - SyncTail.Length, SyncTail.Length).SequenceEqual(SyncTail))
            throw new InvalidDataException("The ZIP has a DEFLATE block without a byte-aligned flush marker.");
    }

    public void Dispose()
    {
        foreach (var block in blocks)
            block?.Encoded.Dispose();
    }

    private sealed class Block
    {
        public readonly byte[] Plain = new byte[BlockSize];
        public readonly MemoryStream Encoded = new(MaxEncodedBlock + 32);
        public int PlainSize;
        public int EncodedSize;
    }
}
