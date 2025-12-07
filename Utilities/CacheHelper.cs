using BomLocalService.Models;
using Microsoft.Extensions.Configuration;

namespace BomLocalService.Utilities;

/// <summary>
/// Utility methods for cache folder validation and operations.
/// </summary>
public static class CacheHelper
{
    /// <summary>
    /// Gets the configured frame count for a data type from configuration.
    /// </summary>
    public static int GetFrameCountForDataType(IConfiguration configuration, CachedDataType dataType)
    {
        var dataTypeName = dataType.ToString();
        var frameCount = configuration.GetValue<int>($"CachedDataTypes:{dataTypeName}:FrameCount", 7);
        return frameCount;
    }

    /// <summary>
    /// Checks if a cache folder has complete data for a specific data type.
    /// </summary>
    public static bool IsCacheFolderCompleteForDataType(string cacheFolderPath, CachedDataType dataType, IConfiguration configuration)
    {
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return false;
        }

        var dataTypeFolder = FilePathHelper.GetDataTypeFolderPath(cacheFolderPath, dataType);
        if (!Directory.Exists(dataTypeFolder))
        {
            return false;
        }

        var expectedFrameCount = GetFrameCountForDataType(configuration, dataType);
        for (int i = 0; i < expectedFrameCount; i++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, dataType, i);
            if (!File.Exists(framePath))
            {
                return false;
            }
        }

        // Check for frames.json in data type folder
        var framesMetadataPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
        if (!File.Exists(framesMetadataPath))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a cache folder is complete (has radar data - for backward compatibility).
    /// </summary>
    public static bool IsCacheFolderComplete(string cacheFolderPath, IConfiguration configuration)
    {
        return IsCacheFolderCompleteForDataType(cacheFolderPath, CachedDataType.Radar, configuration);
    }
}

