using System.Text.Json;
using System.Text.Json.Serialization;
using BomLocalService.Models;
using Microsoft.Extensions.Configuration;

namespace BomLocalService.Utilities;

public sealed record SnapshotValidationResult(bool IsValid, string? ErrorCode, string? Error)
{
    public static SnapshotValidationResult Valid() => new(true, null, null);
    public static SnapshotValidationResult Invalid(string code, string message) => new(false, code, message);
}

/// <summary>
/// Completion marker written inside a published snapshot. The explicit
/// suburb/state fields make new ownership exact without parsing display text.
/// Older markers without them remain readable.
/// </summary>
public sealed class SnapshotMarker
{
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime? PublishedAtUtc { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("suburb")]
    public string? Suburb { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("frameCount")]
    public int? FrameCount { get; set; }
}

/// <summary>
/// Utility methods for cache folder validation and operations.
/// </summary>
public static class CacheHelper
{
    public const int MaxMarkerBytes = 64 * 1024;
    public const int MaxMetadataBytes = 256 * 1024;
    public const int MaxFramesMetadataBytes = 256 * 1024;

    private static readonly JsonSerializerOptions BoundedJsonOptions = new()
    {
        MaxDepth = 16,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false
    };

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
        var fullPath = Path.GetFullPath(cacheFolderPath);
        var root = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? fullPath;
        return ValidateSnapshot(root, fullPath, expectedLocation, dataType, configuration, nowUtc);
    }

    public static SnapshotValidationResult ValidateSnapshot(
        string cacheRoot,
        string cacheFolderPath,
        LocationId expectedLocation,
        CachedDataType dataType,
        IConfiguration configuration,
        DateTime? nowUtc = null)
    {
        if (!TryParseSnapshotFolderName(cacheFolderPath, out var folder) || folder is null)
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_NAME_INVALID", "Snapshot folder name is not recognised");
        }

        if (!folder.Location.Equals(expectedLocation))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_LOCATION_MISMATCH", "Snapshot does not belong to the requested location");
        }

        if (!FilePathHelper.TryResolveDirectChild(
                cacheRoot,
                folder.FolderName,
                out var resolved,
                out var resolveError))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_UNSAFE", resolveError ?? "Snapshot folder is missing or unsafe");
        }

        if (!TryResolveSnapshotIdentity(cacheRoot, folder, out var identity, out var identityError) ||
            identity is null ||
            !identity.Equals(expectedLocation))
        {
            return SnapshotValidationResult.Invalid(
                "SNAPSHOT_LOCATION_MISMATCH",
                identityError ?? "Snapshot identity does not match the requested location");
        }

        var metadataRelative = $"{folder.FolderName}/metadata.json";
        if (!FilePathHelper.TryResolveOwnedPath(cacheRoot, metadataRelative, allowMissingLeaf: false, out var metadataPath, out var metadataError))
        {
            return SnapshotValidationResult.Invalid("METADATA_MISSING", metadataError ?? "metadata.json is missing or unsafe");
        }

        if (!TryReadBoundedJson<LastUpdatedInfo>(metadataPath!, MaxMetadataBytes, out var metadata, out var metadataReadError) ||
            metadata is null)
        {
            return SnapshotValidationResult.Invalid("METADATA_INVALID", metadataReadError ?? "metadata.json is not readable");
        }

        if (!IsUsableTimestamp(metadata.ObservationTime, nowUtc))
        {
            return SnapshotValidationResult.Invalid("METADATA_INVALID", "metadata observation time is missing or unusable");
        }

        var dataTypeFolder = dataType.ToString().ToLowerInvariant();
        var framesRelative = $"{folder.FolderName}/{dataTypeFolder}/frames.json";
        if (!FilePathHelper.TryResolveOwnedPath(cacheRoot, framesRelative, allowMissingLeaf: false, out var framesPath, out var framesError))
        {
            return SnapshotValidationResult.Invalid("FRAMES_METADATA_MISSING", framesError ?? "frames.json is missing or unsafe");
        }

        if (!TryReadBoundedJson<List<FrameMetadata?>>(framesPath!, MaxFramesMetadataBytes, out var frameMetadata, out var framesReadError) ||
            frameMetadata is null)
        {
            return SnapshotValidationResult.Invalid("FRAMES_METADATA_INVALID", framesReadError ?? "frames.json is not readable");
        }

        var expectedFrameCount = GetFrameCountForDataType(configuration, dataType);
        if (frameMetadata.Count != expectedFrameCount)
        {
            return SnapshotValidationResult.Invalid("FRAME_COUNT_MISMATCH", "frames.json does not describe the configured frame count");
        }

        var seen = new HashSet<int>();
        foreach (var frame in frameMetadata)
        {
            if (frame is null ||
                frame.FrameIndex < 0 ||
                frame.FrameIndex >= expectedFrameCount ||
                !seen.Add(frame.FrameIndex))
            {
                return SnapshotValidationResult.Invalid("FRAME_INDEX_INVALID", "frames.json contains an invalid, null, or duplicate frame index");
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
            var frameRelative = $"{folder.FolderName}/{dataTypeFolder}/frame_{index}.png";
            if (!FilePathHelper.TryResolveOwnedPath(cacheRoot, frameRelative, allowMissingLeaf: false, out var framePath, out var frameError) ||
                !File.Exists(framePath))
            {
                return SnapshotValidationResult.Invalid("FRAME_MISSING", frameError ?? $"frame_{index}.png is missing or unsafe");
            }

            if (!PngValidationHelper.IsValidPng(framePath!))
            {
                return SnapshotValidationResult.Invalid("FRAME_INVALID", $"frame_{index}.png is not a readable PNG image");
            }
        }

        var markerRelative = $"{folder.FolderName}/.complete";
        if (!FilePathHelper.TryResolveOwnedPath(cacheRoot, markerRelative, allowMissingLeaf: true, out var markerPath, out var markerError))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_UNSAFE", markerError ?? "completion marker is unsafe");
        }

        if (Directory.Exists(markerPath))
        {
            return SnapshotValidationResult.Invalid("SNAPSHOT_UNSAFE", "completion marker is not a file");
        }

        return SnapshotValidationResult.Valid();
    }

    /// <summary>
    /// Resolves the exact identity from the reversible folder name and, when
    /// present, cross-checks an explicit identity in the completion marker.
    /// Legacy markers without suburb/state are never parsed as identity text.
    /// </summary>
    public static bool TryResolveSnapshotIdentity(
        string cacheRoot,
        ParsedCacheFolder folder,
        out LocationId? identity,
        out string? error)
    {
        identity = null;
        error = null;

        if (folder.IsStaging)
        {
            identity = folder.Location;
            return true;
        }

        var markerRelative = $"{folder.FolderName}/.complete";
        if (!FilePathHelper.TryResolveOwnedPath(cacheRoot, markerRelative, allowMissingLeaf: true, out var markerPath, out error))
        {
            return false;
        }

        if (Directory.Exists(markerPath))
        {
            error = "Completion marker is not a file";
            return false;
        }

        if (!File.Exists(markerPath))
        {
            identity = folder.Location;
            return true;
        }

        if (!TryReadBoundedJson<SnapshotMarker>(markerPath!, MaxMarkerBytes, out var marker, out var markerError) ||
            marker is null)
        {
            error = markerError ?? "Completion marker is not readable";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(marker.Suburb) && !string.IsNullOrWhiteSpace(marker.State))
        {
            if (!LocationId.TryCreate(marker.Suburb, marker.State, out var markerLocation, out var locationError))
            {
                error = locationError ?? "Completion marker identity is invalid";
                return false;
            }

            if (!markerLocation!.Equals(folder.Location))
            {
                error = "Completion marker identity does not match the folder name";
                return false;
            }

            identity = markerLocation;
            return true;
        }

        identity = folder.Location;
        return true;
    }

    /// <summary>
    /// Checks if a cache folder is complete. The expected location is derived
    /// from the folder name, so unknown directories are never considered caches.
    /// </summary>
    public static bool IsCacheFolderCompleteForDataType(string cacheFolderPath, CachedDataType dataType, IConfiguration configuration)
    {
        if (!TryParseSnapshotFolderName(cacheFolderPath, out var parsed))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(cacheFolderPath);
        var root = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? fullPath;
        return ValidateSnapshot(root, fullPath, parsed!.Location, dataType, configuration).IsValid;
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
    /// The reversible v2 grammar, when present, supplies an exact LocationId
    /// without reading the completion marker.
    /// </summary>
    public static bool TryParseSnapshotFolderName(string folderPath, out ParsedCacheFolder? parsed)
    {
        var baseName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        return LocationHelper.TryParseCacheFolderName(baseName, allowStaging: true, out parsed);
    }

    public static bool IsValidPng(string path) => PngValidationHelper.IsValidPng(path);

    public static bool TryReadBoundedJson<T>(
        string path,
        int maxBytes,
        out T? value,
        out string? error)
    {
        value = default;
        error = null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "File does not exist";
                return false;
            }

            if (info.Length <= 0)
            {
                error = "File is empty";
                return false;
            }

            if (info.Length > maxBytes)
            {
                error = "File is larger than the allowed limit";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            value = JsonSerializer.Deserialize<T>(stream, BoundedJsonOptions);
            if (value is null)
            {
                error = "JSON value is null";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"JSON is invalid: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"File could not be read: {ex.Message}";
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
