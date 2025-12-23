using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for managing cached radar screenshot files and metadata.
/// Handles file system operations for storing and retrieving cached BOM radar screenshots.
/// </summary>
public interface ICacheService : ISingletonService
{
    /// <summary>
    /// Gets the cached screenshot folder path and associated metadata for a location and data type.
    /// Searches for cache folders matching the location pattern and loads the corresponding metadata JSON file.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="dataType">The type of cached data to retrieve</param>
    /// <param name="excludeFolder">Optional folder path to exclude from search (e.g., folder currently being written to)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Tuple containing the cache folder path and metadata, or (null, null) if not found</returns>
    Task<(string? cacheFolderPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        CachedDataType dataType,
        string? excludeFolder = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all complete cache folders for a location, ordered by timestamp (oldest first).
    /// Returns basic folder information without loading all frame data.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>List of cache folders with available data types</returns>
    Task<List<CacheFolder>> GetAllCacheFoldersAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets frames from a specific cache folder and data type.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cacheFolderName">The cache folder name</param>
    /// <param name="dataType">The type of cached data to retrieve</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>List of cached frames, or empty list if not found</returns>
    Task<List<CachedFrame>> GetFramesFromCacheFolderAsync(
        string suburb,
        string state,
        string cacheFolderName,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all cached frames for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>List of cached frames, or empty list if not found</returns>
    Task<List<RadarFrame>> GetCachedFramesAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a specific cached frame for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="frameIndex">Frame index (0-6)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>The cached frame, or null if not found</returns>
    Task<RadarFrame?> GetCachedFrameAsync(
        string suburb, 
        string state, 
        int frameIndex,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves metadata JSON file in a cache folder.
    /// Creates a metadata.json file in the specified cache folder.
    /// </summary>
    /// <param name="cacheFolderPath">Full path to the cache folder</param>
    /// <param name="metadata">Metadata to save (observation time, forecast time, weather station, distance)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveMetadataAsync(string cacheFolderPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves frame metadata (frame index and observation time) to a frames.json file in the data type subfolder.
    /// </summary>
    /// <param name="cacheFolderPath">Full path to the cache folder</param>
    /// <param name="dataType">The type of cached data</param>
    /// <param name="frames">List of frames with their metadata</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveFramesMetadataAsync(string cacheFolderPath, CachedDataType dataType, List<RadarFrame> frames, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if cached metadata is still valid (not expired).
    /// Cache is valid if observation time + expiration buffer (typically 15.5 minutes) is still in the future.
    /// BOM updates observations every 15 minutes, so the buffer accounts for timing variations.
    /// </summary>
    /// <param name="metadata">The metadata to validate, or null</param>
    /// <returns>True if metadata exists and cache is still valid, false otherwise</returns>
    bool IsCacheValid(LastUpdatedInfo? metadata);
    
    /// <summary>
    /// Gets the file system path to the most recent cached screenshot for a location.
    /// Returns files ordered by creation time, most recent first.
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
    /// <returns>True if any folders were deleted, false if no cached folders existed</returns>
    Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the cache directory path where screenshots and metadata are stored.
    /// </summary>
    /// <returns>The full path to the cache directory</returns>
    string GetCacheDirectory();
    
    /// <summary>
    /// Cleans up incomplete cache folders (e.g., from a previous crash or restart).
    /// Scans all cache folders and deletes any that are incomplete (missing frames, metadata, etc.).
    /// This should be called on application startup to recover from interrupted cache updates.
    /// </summary>
    /// <returns>The number of incomplete folders that were deleted</returns>
    int CleanupIncompleteCacheFolders();
    
    /// <summary>
    /// Checks if a location is currently being updated (has an active cache folder being written to).
    /// </summary>
    bool IsLocationUpdating(string locationKey);
    
    /// <summary>
    /// Gets the active cache folder path for a location (if one is being written to).
    /// </summary>
    string? GetActiveCacheFolder(string locationKey);
    
    /// <summary>
    /// Sets the active cache folder for a location (indicates a cache update is in progress).
    /// </summary>
    void SetActiveCacheFolder(string locationKey, string cacheFolderPath);
    
    /// <summary>
    /// Clears the active cache folder for a location (indicates cache update is complete).
    /// </summary>
    void ClearActiveCacheFolder(string locationKey);
    
    /// <summary>
    /// Creates a new cache folder for a location and timestamp.
    /// Creates the folder structure and ensures data type subfolders are ready.
    /// </summary>
    string CreateCacheFolder(string suburb, string state, string timestamp);
    
    /// <summary>
    /// Deletes an incomplete cache folder (cleanup on error).
    /// Only deletes if the folder is incomplete (missing required files).
    /// </summary>
    bool TryDeleteIncompleteCacheFolder(string cacheFolderPath);
    
    /// <summary>
    /// Deletes an empty cache folder.
    /// </summary>
    bool TryDeleteEmptyCacheFolder(string cacheFolderPath);
    
    /// <summary>
    /// Gets cache status information for a location and data type without triggering an update.
    /// Returns information about whether cache exists, is valid, is updating, etc.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="dataType">The type of cached data to check</param>
    /// <param name="cacheExpirationMinutes">The number of minutes after observation time that cache expires (will be cast to int)</param>
    /// <param name="cacheManagementCheckIntervalMinutes">The interval in minutes that the background cache management service checks for updates</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Cache status information</returns>
    Task<CacheUpdateStatus> GetCacheStatusAsync(
        string suburb, 
        string state, 
        CachedDataType dataType,
        int cacheExpirationMinutes,
        int cacheManagementCheckIntervalMinutes,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Records progress update for a cache update operation using cacheFolderPath.
    /// This is useful when locationKey is not directly available (e.g., in ScrapingService).
    /// </summary>
    /// <param name="cacheFolderPath">The cache folder path being updated</param>
    /// <param name="phase">The current phase of the update</param>
    /// <param name="currentFrame">The current frame being captured (if in CapturingFrames phase)</param>
    /// <param name="totalFrames">The total number of frames to capture</param>
    void RecordUpdateProgressByFolder(string cacheFolderPath, CacheUpdatePhase phase, int? currentFrame = null, int? totalFrames = null);
    
    /// <summary>
    /// Gets the estimated remaining seconds for an in-progress cache update.
    /// Returns 0 if not updating or no metrics available.
    /// </summary>
    /// <param name="locationKey">The location key (suburb_state)</param>
    /// <returns>Estimated remaining seconds, or 0 if not updating or no metrics</returns>
    int GetEstimatedRemainingSeconds(string locationKey);
    
    /// <summary>
    /// Records step completion timing for metrics tracking.
    /// </summary>
    /// <param name="stepName">The name of the step</param>
    /// <param name="durationSeconds">The duration of the step in seconds</param>
    void RecordStepCompletion(string stepName, double durationSeconds);
    
    /// <summary>
    /// Gets the average duration for a specific step from historical data.
    /// </summary>
    /// <param name="stepName">The name of the step</param>
    /// <returns>Average duration in seconds, or 0 if no data available</returns>
    double GetAverageStepDuration(string stepName);
    
    /// <summary>
    /// Gets step performance metrics for debugging/logging.
    /// </summary>
    /// <returns>Dictionary mapping step names to their average durations</returns>
    Dictionary<string, double> GetStepMetrics();
    
    /// <summary>
    /// Gets the average total duration of cache updates from recent metrics.
    /// Uses median for robustness against outliers.
    /// </summary>
    /// <returns>Average total duration in seconds, or 0 if no data available</returns>
    double GetAverageTotalDuration();
}

