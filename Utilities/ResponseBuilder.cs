using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class ResponseBuilder
{
    /// <summary>
    /// Calculates the next time the background cache management service will check for updates.
    /// Rounds up to the next interval boundary within the current hour, or wraps to next hour if needed.
    /// </summary>
    public static DateTime CalculateNextServiceCheck(int checkIntervalMinutes)
    {
        var now = DateTime.UtcNow;
        var currentMinute = now.Minute;
        
        // Calculate how many complete intervals have elapsed this hour
        var intervalsElapsed = currentMinute / checkIntervalMinutes;
        
        // Next interval starts at (intervalsElapsed + 1) * checkIntervalMinutes
        var nextIntervalStartMinute = (intervalsElapsed + 1) * checkIntervalMinutes;
        
        // If next interval is beyond 60 minutes, wrap to next hour
        if (nextIntervalStartMinute >= 60)
        {
            // Next check is at the start of next hour (00 minutes)
            return now.Date.AddHours(now.Hour + 1).AddMinutes(0);
        }
        
        // Next check is within current hour
        return now.Date.AddHours(now.Hour).AddMinutes(nextIntervalStartMinute);
    }

    /// <summary>
    /// Creates a RadarResponse from a cache folder path, frames, and metadata
    /// </summary>
    /// <param name="cacheManagementCheckIntervalMinutes">The interval in minutes that the background cache management service checks for updates. Used to calculate NextUpdateTime when cache is invalid.</param>
    /// <param name="estimatedUpdateDurationSeconds">Estimated duration in seconds for a cache update to complete. Used to calculate NextUpdateTime when update is in progress.</param>
    public static RadarResponse CreateRadarResponse(
        string cacheFolderPath, 
        List<RadarFrame> frames,
        int cacheManagementCheckIntervalMinutes,
        LastUpdatedInfo? metadata = null,
        string? suburb = null,
        string? state = null,
        bool? cacheIsValid = null,
        DateTime? cacheExpiresAt = null,
        bool isUpdating = false,
        int? estimatedUpdateDurationSeconds = null)
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

        // AbsoluteObservationTime is already set during capture, no calculation needed
        // This ensures consistency with the time series endpoint

        // Calculate NextUpdateTime based on cache status:
        // - If cache is valid: NextUpdateTime = max(CacheExpiresAt, next background service check)
        //   (Background service checks every N minutes, so update might happen at next check after expiry)
        // - If cache is invalid and updating: NextUpdateTime = estimated completion time (~2 minutes)
        // - If cache is invalid and NOT updating: NextUpdateTime = next background service check (based on check interval)
        DateTime? nextUpdateTime = null;
        
        // Calculate next background service check time (rounds up to next check interval)
        var now = DateTime.UtcNow;
        var nextServiceCheck = CalculateNextServiceCheck(cacheManagementCheckIntervalMinutes);
        
        if (isUpdating)
        {
            // Update in progress - estimate completion based on configured/calculated duration
            var durationSeconds = estimatedUpdateDurationSeconds ?? 120; // Default to 2 minutes if not provided
            nextUpdateTime = now.AddSeconds(durationSeconds);
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

