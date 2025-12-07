using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class FilePathHelper
{
    /// <summary>
    /// Gets the cache directory path from configuration or returns default
    /// </summary>
    public static string GetCacheDirectory(IConfiguration configuration)
    {
        return configuration.GetValue<string>("CacheDirectory") 
            ?? Path.Combine(AppContext.BaseDirectory, "cache");
    }

    /// <summary>
    /// Gets the metadata file path for a given screenshot path (legacy method for backward compatibility)
    /// </summary>
    [Obsolete("Use GetMetadataFilePath(string cacheFolderPath) for folder-based storage")]
    public static string GetMetadataFilePathFromScreenshot(string screenshotPath)
    {
        return Path.ChangeExtension(screenshotPath, ".json");
    }

    /// <summary>
    /// Gets the cache file pattern for a location key (e.g., "Pomona_QLD_*.png")
    /// </summary>
    public static string GetCacheFilePattern(string locationKey)
    {
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return $"{safeLocationKey}_*.png";
    }

    /// <summary>
    /// Gets the cache file pattern for suburb and state
    /// </summary>
    public static string GetCacheFilePattern(string suburb, string state)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        return GetCacheFilePattern(locationKey);
    }

    /// <summary>
    /// Gets the cache folder path for a location and timestamp.
    /// Format: "{CacheDirectory}/{LocationKey}_{Timestamp}"
    /// Example: "/app/cache/Pomona_QLD_20251207_000906"
    /// </summary>
    public static string GetCacheFolderPath(string cacheDirectory, string suburb, string state, string timestamp)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return Path.Combine(cacheDirectory, $"{safeLocationKey}_{timestamp}");
    }

    /// <summary>
    /// Gets the data type subfolder path within a cache folder.
    /// Format: "{CacheFolderPath}/{DataType}"
    /// Example: "/app/cache/Pomona_QLD_20251207_000906/radar"
    /// </summary>
    public static string GetDataTypeFolderPath(string cacheFolderPath, CachedDataType dataType)
    {
        var dataTypeFolder = dataType.ToString().ToLowerInvariant();
        return Path.Combine(cacheFolderPath, dataTypeFolder);
    }

    /// <summary>
    /// Gets the frame file path within a data type subfolder.
    /// Format: "{CacheFolderPath}/{DataType}/frame_{FrameIndex}.png"
    /// </summary>
    public static string GetFrameFilePath(string cacheFolderPath, CachedDataType dataType, int frameIndex)
    {
        var dataTypeFolder = GetDataTypeFolderPath(cacheFolderPath, dataType);
        return Path.Combine(dataTypeFolder, $"frame_{frameIndex}.png");
    }

    /// <summary>
    /// Gets the frame file path within a cache folder (legacy - defaults to radar).
    /// Format: "{CacheFolderPath}/radar/frame_{FrameIndex}.png"
    /// </summary>
    [Obsolete("Use GetFrameFilePath with dataType parameter")]
    public static string GetFrameFilePath(string cacheFolderPath, int frameIndex)
    {
        return GetFrameFilePath(cacheFolderPath, CachedDataType.Radar, frameIndex);
    }

    /// <summary>
    /// Gets the metadata file path within a cache folder.
    /// Format: "{CacheFolderPath}/metadata.json"
    /// </summary>
    public static string GetMetadataFilePath(string cacheFolderPath)
    {
        return Path.Combine(cacheFolderPath, "metadata.json");
    }

    /// <summary>
    /// Gets the cache folder pattern for finding existing folders.
    /// Format: "{LocationKey}_*"
    /// </summary>
    public static string GetCacheFolderPattern(string suburb, string state)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return $"{safeLocationKey}_*";
    }

    /// <summary>
    /// Gets the lock file path for a cache folder.
    /// This file indicates the folder is currently being written to.
    /// Format: "{CacheFolderPath}/.writing"
    /// </summary>
    public static string GetCacheLockFilePath(string cacheFolderPath)
    {
        return Path.Combine(cacheFolderPath, ".writing");
    }

    /// <summary>
    /// Gets the frames metadata file path within a data type subfolder.
    /// Format: "{CacheFolderPath}/{DataType}/frames.json"
    /// </summary>
    public static string GetFramesMetadataFilePath(string cacheFolderPath, CachedDataType dataType)
    {
        var dataTypeFolder = GetDataTypeFolderPath(cacheFolderPath, dataType);
        return Path.Combine(dataTypeFolder, "frames.json");
    }

    /// <summary>
    /// Gets the frames metadata file path within a cache folder (legacy - defaults to radar).
    /// Format: "{CacheFolderPath}/radar/frames.json"
    /// </summary>
    [Obsolete("Use GetFramesMetadataFilePath with dataType parameter")]
    public static string GetFramesMetadataFilePath(string cacheFolderPath)
    {
        return GetFramesMetadataFilePath(cacheFolderPath, CachedDataType.Radar);
    }
}

