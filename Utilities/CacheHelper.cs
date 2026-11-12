using System.Buffers.Binary;
using System.Text.Json;
using BomLocalService.Models;
using Microsoft.Extensions.Configuration;

namespace BomLocalService.Utilities;

public sealed record SnapshotValidationResult(bool IsValid, string? ErrorCode, string? Error)
{
    public static SnapshotValidationResult Valid() => new(true, null, null);
    public static SnapshotValidationResult Invalid(string code, string message) => new(false, code, message);
}

/// <summary>
/// Utility methods for cache folder validation and operations.
/// </summary>
public static class CacheHelper
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static int GetFrameCountForDataType(IConfiguration configuration, CachedDataType dataType)
    {
        var dataTypeName = dataType.ToString();
        var frameCountConfig = configuration.GetValue<int?>($"CachedDataTypes:{dataTypeName}:FrameCount");
        if (!frameCountConfig.HasValue)
        {
            throw new InvalidOperationException($"CachedDataTypes:{dataTypeName}:FrameCount configuration is required. Set it in appsettings.json or via CACHEDATATYPES__{dataTypeName.ToUpperInvariant()}__FRAMECOUNT environment variable.");
        }

        if (frameCountConfig.Value <= 0 || frameCountConfig.Value > 1000)
        {
            throw new InvalidOperationException($"CachedDataTypes:{dataTypeName}:FrameCount must be between 1 and 1000.");
        }

        return frameCountConfig.Value;
    }

    /// <summary>
    /// Validates a complete snapshot: readable metadata, expected image files,
    /// coherent frame indices and usable timestamps. Legacy snapshots without a
    /// completion marker remain valid when their files are complete.
    /// </summary>
    public static SnapshotValidationResult ValidateSnapshot(
        string cacheFolderPath,
        LocationId expectedLocation,
        CachedDataType dataType,
        IConfiguration configuration,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_MISSING", "Snapshot folder does not exist");
        }

        if (FilePathHelper.IsSymlinkOrReparsePoint(cacheFolderPath))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_UNSAFE", "Snapshot folder is a symbolic link");
        }

        if (!TryParseSnapshotFolderName(cacheFolderPath, out var parsed) ||
            !parsed!.Location.Equals(expectedLocation))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_LOCATION_MISMATCH", "Snapshot does not belong to the requested location");
        }

        if (!FilePathHelper.TryResolveOwnedChild(
                Path.GetDirectoryName(cacheFolderPath) ?? cacheFolderPath,
                cacheFolderPath,
                "metadata.json",
                out var metadataPath,
                out _) ||
            !File.Exists(metadataPath))
        {
            return SnapshotValidationResult.Invalid("METADATA_MISSING", "metadata.json is missing or unsafe");
        }

        LastUpdatedInfo? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<LastUpdatedInfo>(File.ReadAllText(metadataPath!));
        }
        catch (Exception)
        {
            return SnapshotValidationResult.Invalid("METADATA_INVALID", "metadata.json is not readable");
        }

        if (metadata is null || !IsUsableTimestamp(metadata.ObservationTime, nowUtc))
        {
            return SnapshotValidationResult.Invalid("METADATA_INVALID", "metadata observation time is missing or unusable");
        }

        var framesPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
        if (FilePathHelper.IsSymlinkOrReparsePoint(framesPath) || !File.Exists(framesPath))
        {
            return SnapshotValidationResult.Invalid("FRAMES_METADATA_MISSING", "frames.json is missing or unsafe");
        }

        List<FrameMetadata>? frameMetadata;
        try
        {
            frameMetadata = JsonSerializer.Deserialize<List<FrameMetadata>>(File.ReadAllText(framesPath));
        }
        catch (Exception)
        {
            return SnapshotValidationResult.Invalid("FRAMES_METADATA_INVALID", "frames.json is not readable");
        }

        var expectedFrameCount = GetFrameCountForDataType(configuration, dataType);
        if (frameMetadata is null || frameMetadata.Count != expectedFrameCount)
        {
            return SnapshotValidationResult.Invalid("FRAME_COUNT_MISMATCH", "frames.json does not describe the configured frame count");
        }

        var seen = new HashSet<int>();
        foreach (var frame in frameMetadata)
        {
            if (frame.FrameIndex < 0 || frame.FrameIndex >= expectedFrameCount || !seen.Add(frame.FrameIndex))
            {
                return SnapshotValidationResult.Invalid("FRAME_INDEX_INVALID", "frames.json contains an invalid or duplicate frame index");
            }

            if (!IsUsableTimestamp(frame.ObservationTime, nowUtc))
            {
                return SnapshotValidationResult.Invalid("FRAME_TIME_INVALID", "frames.json contains an unusable observation time");
            }
        }

        if (seen.Count != expectedFrameCount)
        {
            return SnapshotValidationResult.Invalid("FRAME_INDEX_INVALID", "frames.json does not contain every configured frame index");
        }

        for (var index = 0; index < expectedFrameCount; index++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, dataType, index);
            if (FilePathHelper.IsSymlinkOrReparsePoint(framePath) || !File.Exists(framePath))
            {
                return SnapshotValidationResult.Invalid("FRAME_MISSING", $"frame_{index}.png is missing or unsafe");
            }

            if (!IsValidPng(framePath))
            {
                return SnapshotValidationResult.Invalid("FRAME_INVALID", $"frame_{index}.png is not a readable PNG image");
            }
        }

        var markerPath = FilePathHelper.GetSnapshotCompleteMarkerPath(cacheFolderPath);
        if (File.Exists(markerPath) && FilePathHelper.IsSymlinkOrReparsePoint(markerPath))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_UNSAFE", "completion marker is a symbolic link");
        }

        return SnapshotValidationResult.Valid();
    }

    /// <summary>
    /// Checks if a cache folder is complete. The expected location is derived
    /// from the folder name, so unknown directories are never considered caches.
    /// </summary>
    public static bool IsCacheFolderCompleteForDataType(string cacheFolderPath, CachedDataType dataType, IConfiguration configuration)
    {
        if (!LocationHelper.TryParseCacheFolderName(Path.GetFileName(cacheFolderPath.TrimEnd(Path.DirectorySeparatorChar)), out var parsed))
        {
            return false;
        }

        return ValidateSnapshot(cacheFolderPath, parsed!.Location, dataType, configuration).IsValid;
    }

    public static bool IsCacheFolderComplete(string cacheFolderPath, IConfiguration configuration) =>
        IsCacheFolderCompleteForDataType(cacheFolderPath, CachedDataType.Radar, configuration);

    public static bool IsUsableTimestamp(DateTime value, DateTime? nowUtc = null)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            value = value.ToUniversalTime();
        }

        var now = nowUtc ?? DateTime.UtcNow;
        return value.Year >= 2000 && value <= now.AddDays(1);
    }

    /// <summary>
    /// Parses a final snapshot name or a staging name with an 8+ hex suffix.
    /// </summary>
    public static bool TryParseSnapshotFolderName(string folderPath, out ParsedCacheFolder? parsed)
    {
        var baseName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        if (LocationHelper.TryParseCacheFolderName(baseName, out parsed))
        {
            return true;
        }

        var separator = baseName.LastIndexOf('_');
        if (separator <= 0)
        {
            parsed = null;
            return false;
        }

        var suffix = baseName[(separator + 1)..];
        if (suffix.Length >= 8 && suffix.All(Uri.IsHexDigit))
        {
            return LocationHelper.TryParseCacheFolderName(baseName[..separator], out parsed);
        }

        parsed = null;
        return false;
    }

    private static bool IsValidPng(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 24)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);

            if (!header[..8].SequenceEqual(PngSignature))
            {
                return false;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Calculates the estimated cache update duration in seconds.
    /// </summary>
    public static int GetEstimatedUpdateDurationSeconds(IConfiguration configuration, CachedDataType dataType = CachedDataType.Radar)
    {
        var frameCount = GetFrameCountForDataType(configuration, dataType);

        var tileRenderWaitMs = configuration.GetValue<int?>("Screenshot:TileRenderWaitMs")
            ?? throw new InvalidOperationException("Screenshot:TileRenderWaitMs configuration is required. Set it in appsettings.json or via SCREENSHOT__TILERENDERWAITMS environment variable.");

        var dynamicContentWaitMs = configuration.GetValue<int?>("Screenshot:DynamicContentWaitMs")
            ?? throw new InvalidOperationException("Screenshot:DynamicContentWaitMs configuration is required. Set it in appsettings.json or via SCREENSHOT__DYNAMICCONTENTWAITMS environment variable.");

        var perFrameSeconds = (tileRenderWaitMs + 1500) / 1000.0;
        var baseOverheadSeconds = 15 + (dynamicContentWaitMs / 1000.0);
        var estimatedSeconds = (int)Math.Ceiling(baseOverheadSeconds + (frameCount * perFrameSeconds));

        return estimatedSeconds;
    }
}
