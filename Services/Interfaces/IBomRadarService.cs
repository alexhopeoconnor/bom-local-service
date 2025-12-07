using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Main service interface for BOM radar screenshot operations.
/// Orchestrates cache management, browser automation, and web scraping to provide radar screenshots for Australian locations.
/// </summary>
public interface IBomRadarService
{
    /// <summary>
    /// Gets cached radar data for a location.
    /// Returns the radar response with all frames if available in cache, otherwise returns null.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Radar response with frames and metadata, or null if not cached</returns>
    Task<RadarResponse?> GetCachedRadarAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a cache update for a location.
    /// If cache is missing or expired, initiates a background update and returns status information.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Status information about the cache update operation</returns>
    Task<CacheUpdateStatus> TriggerCacheUpdateAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata about the cached radar data for a location.
    /// Returns observation time, forecast time, weather station, and distance information.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Last updated information, or null if no cached data exists</returns>
    Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file system path to the cached screenshot for a location.
    /// Returns empty string if no cached screenshot exists.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>File system path to the cached screenshot, or empty string if not found</returns>
    Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cached folders (containing frames and metadata) for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>True if folders were deleted, false if no cached folders existed</returns>
    Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all cached frames for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>List of cached frames with URLs, or null if no frames exist</returns>
    Task<List<RadarFrame>?> GetCachedFramesAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a specific cached frame for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="frameIndex">Frame index (0-6)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>The cached frame, or null if not found</returns>
    Task<RadarFrame?> GetCachedFrameAsync(string suburb, string state, int frameIndex, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets information about the available cache range for a location (oldest and newest cache folders).
    /// </summary>
    Task<CacheRange> GetCacheRangeAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets historical radar data (frames from multiple cache folders) between specified timestamps.
    /// Radar frames can be joined across cache folders because they represent a historical time series.
    /// </summary>
    Task<RadarTimeSeriesResponse> GetRadarTimeSeriesAsync(
        string suburb, 
        string state, 
        DateTime? startTime, 
        DateTime? endTime, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a specific radar frame from a specific cache folder.
    /// </summary>
    Task<RadarFrame?> GetFrameFromCacheFolderAsync(
        string suburb, 
        string state, 
        string cacheFolderName,
        int frameIndex, 
        CancellationToken cancellationToken = default);
}

