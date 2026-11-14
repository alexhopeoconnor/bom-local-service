using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using StbImageSharp;

namespace BomLocalService.Utilities;

/// <summary>
/// Bounded PNG validation. The preflight verifies chunk structure, CRCs, and
/// exact inflated scanline size before the managed decoder is invoked, so a
/// header-only file or a deflate bomb cannot cause a large allocation.
/// </summary>
public static class PngValidationHelper
{
    public const int MaxPngBytes = 16 * 1024 * 1024;
    public const uint MaxDimension = 8192;
    public const ulong MaxPixels = 16_777_216;

    private const int MaxChunks = 1024;
    private const ulong MaxInflatedBytes = 192UL * 1024 * 1024;
    // A default 24-hour history for two locations exceeds 1,300 PNGs. Keep
    // enough fingerprints for that working set; these entries contain no pixels.
    private const int MaxCacheEntries = 8192;

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (long Length, long LastWriteTicks, bool Valid)> ValidationCache =
        new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheInsertionOrder = new();

    public static bool IsValidPng(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxPngBytes)
            {
                return false;
            }

            (long Length, long LastWriteTicks) fingerprint = (info.Length, info.LastWriteTimeUtc.Ticks);
            lock (CacheGate)
            {
                if (ValidationCache.TryGetValue(path, out var cached) &&
                    cached.Length == fingerprint.Length &&
                    cached.LastWriteTicks == fingerprint.LastWriteTicks)
                {
                    return cached.Valid;
                }
            }

            var bytes = File.ReadAllBytes(path);
            var valid = TryValidate(bytes, out _);

            lock (CacheGate)
            {
                if (!ValidationCache.ContainsKey(path))
                {
                    if (ValidationCache.Count >= MaxCacheEntries)
                    {
                        ValidationCache.Remove(CacheInsertionOrder.Dequeue());
                    }

                    CacheInsertionOrder.Enqueue(path);
                }

                ValidationCache[path] = (fingerprint.Length, fingerprint.LastWriteTicks, valid);
            }

            return valid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryValidate(byte[] bytes, out string? error)
    {
        error = null;

        if (bytes is null || bytes.Length < 8 + 12 + 13 + 12 || bytes.Length > MaxPngBytes)
        {
            error = "PNG length is invalid";
            return false;
        }

        if (!bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            error = "PNG signature is invalid";
            return false;
        }

        var offset = Signature.Length;
        var chunkCount = 0;
        var seenHeader = false;
        var seenPalette = false;
        var seenData = false;
        var seenEnd = false;
        var dataEnded = false;
        uint width = 0;
        uint height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        using var compressedData = new MemoryStream();

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                error = "PNG chunk header is truncated";
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue)
            {
                error = "PNG chunk is too large";
                return false;
            }

            var chunkEnd = (long)offset + 12 + length;
            if (chunkEnd > bytes.Length)
            {
                error = "PNG chunk exceeds the file";
                return false;
            }

            chunkCount++;
            if (chunkCount > MaxChunks)
            {
                error = "PNG contains too many chunks";
                return false;
            }

            var typeBytes = bytes.AsSpan(offset + 4, 4);
            if (!IsAsciiLetter(typeBytes[0]) ||
                !IsAsciiLetter(typeBytes[1]) ||
                !IsAsciiLetter(typeBytes[2]) ||
                !IsAsciiLetter(typeBytes[3]))
            {
                error = "PNG chunk type is invalid";
                return false;
            }

            var type = Encoding.ASCII.GetString(typeBytes);
            var data = bytes.AsSpan(offset + 8, (int)length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + (int)length, 4));
            if (ComputeCrc(bytes.AsSpan(offset + 4, 4 + (int)length)) != expectedCrc)
            {
                error = $"PNG chunk {type} has an invalid CRC";
                return false;
            }

            if (!seenHeader)
            {
                if (!string.Equals(type, "IHDR", StringComparison.Ordinal) ||
                    !TryParseHeader(data, out width, out height, out bitDepth, out colorType, out interlace, out error))
                {
                    return false;
                }

                seenHeader = true;
            }
            else
            {
                if (string.Equals(type, "IHDR", StringComparison.Ordinal))
                {
                    error = "PNG contains more than one IHDR";
                    return false;
                }

                if (string.Equals(type, "PLTE", StringComparison.Ordinal))
                {
                    if (seenPalette || seenData)
                    {
                        error = "PNG contains multiple or misplaced PLTE chunks";
                        return false;
                    }

                    if (!TryValidatePalette(data, colorType, bitDepth, out error))
                    {
                        return false;
                    }

                    seenPalette = true;
                }
                else if (string.Equals(type, "IDAT", StringComparison.Ordinal))
                {
                    if (seenEnd || dataEnded || length == 0)
                    {
                        error = "PNG IDAT sequence is invalid";
                        return false;
                    }

                    compressedData.Write(data);
                    seenData = true;
                }
                else if (string.Equals(type, "IEND", StringComparison.Ordinal))
                {
                    if (seenEnd || length != 0 || !seenData)
                    {
                        error = "PNG IEND is invalid";
                        return false;
                    }

                    seenEnd = true;
                    offset = (int)chunkEnd;
                    if (offset != bytes.Length)
                    {
                        error = "PNG contains trailing data after IEND";
                        return false;
                    }

                    break;
                }
                else
                {
                    if (seenData)
                    {
                        dataEnded = true;
                    }

                    if ((typeBytes[0] & 0x20) == 0)
                    {
                        error = $"PNG contains an unknown critical chunk {type}";
                        return false;
                    }
                }
            }

            offset = (int)chunkEnd;
        }

        if (!seenHeader || !seenData || !seenEnd)
        {
            error = "PNG is missing IHDR, IDAT, or IEND";
            return false;
        }

        if (colorType == 3 && !seenPalette)
        {
            error = "Palette PNG is missing PLTE";
            return false;
        }

        var expectedInflated = ExpectedFilteredBytes(width, height, bitDepth, colorType, interlace);
        if (expectedInflated == 0 || expectedInflated > MaxInflatedBytes)
        {
            error = "PNG inflated size is not bounded";
            return false;
        }

        if (compressedData.Length == 0 || compressedData.Length > MaxPngBytes)
        {
            error = "PNG compressed data is invalid";
            return false;
        }

        var expectedInflatedLong = checked((long)expectedInflated);
        try
        {
            compressedData.Position = 0;
            using var zlib = new ZLibStream(compressedData, CompressionMode.Decompress);
            using var sink = new BoundedWriteStream(expectedInflatedLong);
            zlib.CopyTo(sink);

            if (sink.Written != expectedInflatedLong)
            {
                error = "PNG inflated size does not match the declared image";
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            error = $"PNG compressed data is invalid: {ex.Message}";
            return false;
        }

        try
        {
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (image is null ||
                image.Width != (int)width ||
                image.Height != (int)height ||
                image.Data is null ||
                image.Data.LongLength != (long)width * height * 4)
            {
                error = "PNG could not be decoded consistently";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"PNG decoder rejected the file: {ex.Message}";
            return false;
        }

        return true;
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        out uint width,
        out uint height,
        out byte bitDepth,
        out byte colorType,
        out byte interlace,
        out string? error)
    {
        width = 0;
        height = 0;
        bitDepth = 0;
        colorType = 0;
        interlace = 0;
        error = null;

        if (data.Length != 13)
        {
            error = "PNG IHDR length is invalid";
            return false;
        }

        width = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        bitDepth = data[8];
        colorType = data[9];
        var compression = data[10];
        var filter = data[11];
        interlace = data[12];

        if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
        {
            error = "PNG dimensions are invalid";
            return false;
        }

        if ((ulong)width * height > MaxPixels)
        {
            error = "PNG pixel count is too large";
            return false;
        }

        if (compression != 0 || filter != 0 || interlace > 1)
        {
            error = "PNG compression, filter, or interlace mode is invalid";
            return false;
        }

        var validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };

        if (!validDepth)
        {
            error = "PNG color type or bit depth is invalid";
            return false;
        }

        return true;
    }

    private static bool TryValidatePalette(
        ReadOnlySpan<byte> data,
        byte colorType,
        byte bitDepth,
        out string? error)
    {
        error = null;

        if (colorType is 0 or 4)
        {
            error = "PNG palette is not allowed for grayscale images";
            return false;
        }

        if (data.Length is < 3 or > 768 || data.Length % 3 != 0)
        {
            error = "PNG palette length is invalid";
            return false;
        }

        if (colorType == 3 && data.Length / 3 > (1 << bitDepth))
        {
            error = "PNG palette has too many entries";
            return false;
        }

        return true;
    }

    private static ulong ExpectedFilteredBytes(
        uint width,
        uint height,
        byte bitDepth,
        byte colorType,
        byte interlace)
    {
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0
        };

        var bitsPerPixel = (ulong)channels * bitDepth;
        if (bitsPerPixel == 0)
        {
            return 0;
        }

        if (interlace == 0)
        {
            return (ulong)height * (1 + ((ulong)width * bitsPerPixel + 7) / 8);
        }

        var xStart = new[] { 0, 4, 0, 2, 0, 1, 0 };
        var yStart = new[] { 0, 0, 4, 0, 2, 0, 1 };
        var xStep = new[] { 8, 8, 4, 4, 2, 2, 1 };
        var yStep = new[] { 8, 8, 8, 4, 4, 2, 2 };

        ulong total = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            if (width <= (uint)xStart[pass] || height <= (uint)yStart[pass])
            {
                continue;
            }

            var passWidth = ((ulong)width - (uint)xStart[pass] + (uint)xStep[pass] - 1) / (uint)xStep[pass];
            var passHeight = ((ulong)height - (uint)yStart[pass] + (uint)yStep[pass] - 1) / (uint)yStep[pass];
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            total += passHeight * (1 + (passWidth * bitsPerPixel + 7) / 8);
        }

        return total;
    }

    private static bool IsAsciiLetter(byte value) =>
        (value is >= (byte)'A' and <= (byte)'Z') ||
        (value is >= (byte)'a' and <= (byte)'z');

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < (uint)table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private sealed class BoundedWriteStream : Stream
    {
        private readonly long _limit;

        public BoundedWriteStream(long limit)
        {
            _limit = limit;
        }

        public long Written { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Written + buffer.Length > _limit)
            {
                throw new InvalidDataException("PNG inflated data exceeds the declared image size");
            }

            Written += buffer.Length;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
