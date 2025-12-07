using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class ResponseBuilder
{
    /// <summary>
    /// Creates a RadarResponse from a cache folder path, frames, and metadata
    /// </summary>
    /// <param name="cacheManagementCheckIntervalMinutes">The interval in minutes that the background cache management service checks for updates. Used to calculate NextUpdateTime when cache is invalid.</param>
    public static RadarResponse CreateRadarResponse(
        string cacheFolderPath, 
        List<RadarFrame> frames,
        LastUpdatedInfo? metadata = null,
        string? suburb = null,
        string? state = null,
        bool? cacheIsValid = null,
        DateTime? cacheExpiresAt = null,
        bool isUpdating = false,
        int cacheManagementCheckIntervalMinutes = 5)
    {
        var folderInfo = new DirectoryInfo(cacheFolderPath);
        var lastWriteTime = folderInfo.Exists 
            ? folderInfo.LastWriteTime 
            : DateTime.UtcNow;

        // Generate URLs for each frame if suburb and state are provided
        if (!string.IsNullOrEmpty(suburb) && !string.IsNullOrEmpty(state))
        {
            var encodedSuburb = Uri.EscapeDataString(suburb);
            var encodedState = Uri.EscapeDataString(state);
            foreach (var frame in frames)
            {
                frame.ImageUrl = $"/api/radar/{encodedSuburb}/{encodedState}/frame/{frame.FrameIndex}";
            }
        }

        // Calculate NextUpdateTime based on cache status:
        // - If cache is valid: NextUpdateTime = max(CacheExpiresAt, next background service check)
        //   (Background service checks every N minutes, so update might happen at next check after expiry)
        // - If cache is invalid and updating: NextUpdateTime = estimated completion time (~2 minutes)
        // - If cache is invalid and NOT updating: NextUpdateTime = next background service check (based on check interval)
        DateTime? nextUpdateTime = null;
        
        // Calculate next background service check time (rounds up to next check interval)
        var now = DateTime.UtcNow;
        var minutesUntilNextCheck = cacheManagementCheckIntervalMinutes - (now.Minute % cacheManagementCheckIntervalMinutes);
        var nextServiceCheck = now.AddMinutes(minutesUntilNextCheck);
        
        if (isUpdating)
        {
            // Update in progress - estimate completion in ~2 minutes
            nextUpdateTime = now.AddMinutes(2);
        }
        else if (cacheIsValid == true && cacheExpiresAt.HasValue)
        {
            // Cache is valid - next update will be when cache expires OR next service check, whichever is later
            // This accounts for the fact that the background service only checks every N minutes
            nextUpdateTime = cacheExpiresAt.Value > nextServiceCheck ? cacheExpiresAt.Value : nextServiceCheck;
        }
        else if (cacheIsValid == false)
        {
            // Cache is invalid and not updating - next update will be at next background service check
            nextUpdateTime = nextServiceCheck;
        }

        var response = new RadarResponse
        {
            Frames = frames,
            LastUpdated = lastWriteTime,
            ObservationTime = metadata?.ObservationTime ?? DateTime.UtcNow,
            ForecastTime = metadata?.ForecastTime ?? DateTime.UtcNow,
            WeatherStation = metadata?.WeatherStation,
            Distance = metadata?.Distance,
            CacheIsValid = cacheIsValid ?? false,
            CacheExpiresAt = cacheExpiresAt,
            IsUpdating = isUpdating,
            NextUpdateTime = nextUpdateTime
        };

        return response;
    }
    
    /// <summary>
    /// Legacy method for backward compatibility - creates response with single frame
    /// </summary>
    [Obsolete("Use CreateRadarResponse with cacheFolderPath and frames")]
    public static RadarResponse CreateRadarResponse(
        string imagePath, 
        LastUpdatedInfo? metadata = null)
    {
        var lastWriteTime = File.Exists(imagePath) 
            ? File.GetLastWriteTime(imagePath) 
            : DateTime.UtcNow;

        if (metadata == null)
        {
            return new RadarResponse
            {
                Frames = new List<RadarFrame>(),
                LastUpdated = lastWriteTime,
                ObservationTime = DateTime.UtcNow,
                ForecastTime = DateTime.UtcNow
            };
        }

        return new RadarResponse
        {
            Frames = new List<RadarFrame>(),
            LastUpdated = lastWriteTime,
            ObservationTime = metadata.ObservationTime,
            ForecastTime = metadata.ForecastTime,
            WeatherStation = metadata.WeatherStation,
            Distance = metadata.Distance
        };
    }
}

