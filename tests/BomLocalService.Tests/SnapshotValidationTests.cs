using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BomLocalService.Models;
using BomLocalService.Tests.Infrastructure;
using BomLocalService.Utilities;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BomLocalService.Tests;

public class SnapshotValidationTests
{
    [Fact]
    public void Previously_valid_png_is_revalidated_after_file_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-png-memo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "frame.png");
            var bytes = TestPng.Create(10, 20, 30);
            File.WriteAllBytes(path, bytes);
            Assert.True(PngValidationHelper.IsValidPng(path));
            var previousWrite = File.GetLastWriteTimeUtc(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, previousWrite.AddSeconds(2));

            Assert.False(PngValidationHelper.IsValidPng(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Valid_png_passes_bounded_validation()
    {
        Assert.True(PngValidationHelper.TryValidate(TestPng.Create(10, 20, 30), out var error), error);
    }

    [Fact]
    public void Header_only_png_is_rejected()
    {
        Assert.False(PngValidationHelper.TryValidate(TestPngBytes.CreateHeaderOnly(), out _));
    }

    [Fact]
    public void Corrupt_crc_is_rejected()
    {
        var bytes = TestPng.Create(10, 20, 30);
        bytes[^1] ^= 0xFF;
        Assert.False(PngValidationHelper.TryValidate(bytes, out _));
    }

    [Fact]
    public void Inflated_data_larger_than_declared_image_is_rejected()
    {
        var bomb = TestPngBytes.CreateWithRawScanlines(1, 1, new byte[1024 * 1024]);
        Assert.False(PngValidationHelper.TryValidate(bomb, out _));
    }

    [Fact]
    public void Null_frame_elements_invalidate_snapshot_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTime.UtcNow;
            var observations = Enumerable.Range(0, 7).Select(index => now.AddMinutes(-35 + index * 5)).ToArray();
            var folder = TestCache.SeedSnapshot(root, "Pomona", "QLD", now.AddMinutes(-5), now, observations);
            File.WriteAllText(
                Path.Combine(folder, "radar", "frames.json"),
                "[" + string.Join(",", Enumerable.Repeat("null", 7)) + "]");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CachedDataTypes:Radar:FrameCount"] = "7"
                })
                .Build();

            var location = LocationId.Create("Pomona", "QLD");
            var result = CacheHelper.ValidateSnapshot(root, folder, location, CachedDataType.Radar, configuration, now);

            Assert.False(result.IsValid);
            Assert.Equal("FRAME_INDEX_INVALID", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static class TestPngBytes
    {
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static byte[] CreateHeaderOnly()
        {
            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
            header[8] = 8;
            header[9] = 2;
            WriteChunk(output, "IHDR", header);
            return output.ToArray();
        }

        public static byte[] CreateWithRawScanlines(uint width, uint height, byte[] raw)
        {
            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
            header[8] = 8;
            header[9] = 2;
            WriteChunk(output, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);

            var typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            var crc = Crc32(typeBytes, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint Crc32(byte[] first, byte[] second)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in first)
            {
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            foreach (var value in second)
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
    }
}
