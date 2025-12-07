using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BomLocalService.Services;

public class CacheService : ICacheService
{
    private readonly ILogger<CacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly double _cacheExpirationMinutes;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, string> _activeCacheFolders = new(); // locationKey -> cacheFolderPath

    public CacheService(ILogger<CacheService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
        
        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Gets the cached screenshot folder path and metadata for a location and data type
    /// </summary>
    public async Task<(string? cacheFolderPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        CachedDataType dataType,
        string? excludeFolder = null,
        CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFolderPattern(suburb, state);
        _logger.LogDebug("Looking for cached folders matching pattern: {Pattern} in directory: {Directory}", pattern, _cacheDirectory);
        
        var folders = Directory.GetDirectories(_cacheDirectory, pattern)
            .OrderByDescending(f => 
            {
                // Try to extract timestamp from folder name
                var folderName = Path.GetFileName(f);
                var timestamp = LocationHelper.ParseTimestampFromFilename(folderName);
                if (timestamp.HasValue)
                {
                    return timestamp.Value;
                }
                // Fallback to folder creation time
                return Directory.GetCreationTime(f);
            })
            .ToList();

        // Find the first complete cache folder for the specified data type
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            
            // Skip the folder if it's being excluded (currently being written to)
            if (!string.IsNullOrEmpty(excludeFolder) && Path.GetFullPath(folder).Equals(Path.GetFullPath(excludeFolder), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping excluded cache folder (being written to): {Folder}", folder);
                continue;
            }
            
            // Check if folder is complete for the specified data type
            if (!CacheHelper.IsCacheFolderCompleteForDataType(folder, dataType, _configuration))
            {
                _logger.LogDebug("Skipping incomplete cache folder for {DataType}: {Folder}", dataType, folder);
                continue; // Skip incomplete folders (currently being written to)
            }
            
            // This folder is complete
            var metadata = await LoadMetadataAsync(folder, cancellationToken);
            return (folder, metadata);
        }

        // No complete cache folder found
        return (null, null);
    }
    
    /// <summary>
    /// Gets all cached frames for a location
    /// </summary>
    public async Task<List<RadarFrame>> GetCachedFramesAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default)
    {
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, null, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return new List<RadarFrame>();
        }
        
        var cachedFrames = await GetFramesFromCacheFolderAsync(suburb, state, Path.GetFileName(cacheFolderPath), CachedDataType.Radar, cancellationToken);
        return cachedFrames.Cast<RadarFrame>().ToList();
    }
    
    /// <summary>
    /// Gets a specific cached frame for a location
    /// </summary>
    public async Task<RadarFrame?> GetCachedFrameAsync(
        string suburb, 
        string state, 
        int frameIndex,
        CancellationToken cancellationToken = default)
    {
        var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, CachedDataType.Radar);
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            return null;
        }
        
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, null, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath))
        {
            return null;
        }
        
        var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, CachedDataType.Radar, frameIndex);
        if (!File.Exists(framePath))
        {
            return null;
        }
        
        // Load frame metadata to get accurate minutesAgo
        var framesMetadata = await LoadFramesMetadataAsync(cacheFolderPath, CachedDataType.Radar, cancellationToken);
        var frameMetadata = framesMetadata.FirstOrDefault(f => f.FrameIndex == frameIndex);
        var minutesAgo = frameMetadata != null 
            ? frameMetadata.MinutesAgo 
            : 40 - (frameIndex * 5);
            
        return new RadarFrame
        {
            FrameIndex = frameIndex,
            ImagePath = framePath,
            MinutesAgo = minutesAgo
        };
    }

    /// <summary>
    /// Saves metadata in a cache folder
    /// </summary>
    public async Task SaveMetadataAsync(string cacheFolderPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadataPath = FilePathHelper.GetMetadataFilePath(cacheFolderPath);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            // Use CancellationToken.None for file writes to avoid cancellation issues
            // The frames are already saved, metadata save is best-effort
            await File.WriteAllTextAsync(metadataPath, json, CancellationToken.None);
            _logger.LogDebug("Saved metadata to: {Path}", metadataPath);
        }
        catch (Exception ex)
        {
            // Log but don't fail - frames are already saved
            _logger.LogWarning(ex, "Failed to save metadata for folder: {Path}", cacheFolderPath);
        }
    }
    
    /// <summary>
    /// Saves frame metadata to frames.json in the data type subfolder
    /// </summary>
    public async Task SaveFramesMetadataAsync(string cacheFolderPath, CachedDataType dataType, List<RadarFrame> frames, CancellationToken cancellationToken = default)
    {
        try
        {
            var framesMetadata = frames.Select(f => new FrameMetadata
            {
                FrameIndex = f.FrameIndex,
                MinutesAgo = f.MinutesAgo
            }).ToList();
            
            var framesPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
            var framesDir = Path.GetDirectoryName(framesPath);
            if (!string.IsNullOrEmpty(framesDir) && !Directory.Exists(framesDir))
            {
                Directory.CreateDirectory(framesDir);
            }
            
            var json = JsonSerializer.Serialize(framesMetadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(framesPath, json, CancellationToken.None);
            _logger.LogDebug("Saved frames metadata to: {Path}", framesPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save frames metadata for folder: {Path}", cacheFolderPath);
        }
    }
    
    /// <summary>
    /// Loads frame metadata from frames.json in the data type subfolder
    /// </summary>
    private async Task<List<FrameMetadata>> LoadFramesMetadataAsync(string cacheFolderPath, CachedDataType dataType, CancellationToken cancellationToken = default)
    {
        var framesPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
        if (!File.Exists(framesPath))
        {
            return new List<FrameMetadata>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(framesPath, cancellationToken);
            var framesMetadata = JsonSerializer.Deserialize<List<FrameMetadata>>(json);
            return framesMetadata ?? new List<FrameMetadata>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load frames metadata from: {Path}", framesPath);
            return new List<FrameMetadata>();
        }
    }

    /// <summary>
    /// Loads metadata from a cache folder
    /// </summary>
    private async Task<LastUpdatedInfo?> LoadMetadataAsync(string cacheFolderPath, CancellationToken cancellationToken = default)
    {
        var metadataPath = FilePathHelper.GetMetadataFilePath(cacheFolderPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return JsonSerializer.Deserialize<LastUpdatedInfo>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load metadata from: {Path}", metadataPath);
            return null;
        }
    }

    /// <summary>
    /// Checks if cache is still valid based on observation time
    /// </summary>
    public bool IsCacheValid(LastUpdatedInfo? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        // BOM updates observations at :00, :15, :30, :45 (every 15 minutes by default)
        // Cache expiration includes buffer (e.g., 15.5 minutes = 15 min + 30 sec buffer)
        var nextUpdateTime = metadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        return DateTime.UtcNow < nextUpdateTime;
    }

    /// <summary>
    /// Gets the path to the cached screenshot for a location (returns first frame path for backward compatibility)
    /// </summary>
    public async Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var frames = await GetCachedFramesAsync(suburb, state, cancellationToken);
        return frames.FirstOrDefault()?.ImagePath ?? string.Empty;
    }

    /// <summary>
    /// Deletes all cached folders for a location
    /// </summary>
    public Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFolderPattern(suburb, state);
        var deleted = false;

        try
        {
            // Delete all folders matching the pattern
            var folders = Directory.GetDirectories(_cacheDirectory, pattern);
            foreach (var folder in folders)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _logger.LogInformation("Deleted cached folder: {Folder}", folder);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete folder: {Folder}", folder);
                }
            }

            if (deleted)
            {
                _logger.LogInformation("Deleted all cached folders for location: {Suburb}, {State}", suburb, state);
            }
            else
            {
                _logger.LogDebug("No cached folders found to delete for location: {Suburb}, {State}", suburb, state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location: {Suburb}, {State}", suburb, state);
            return Task.FromResult(false);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Gets all complete cache folders for a location, ordered by timestamp (oldest first)
    /// </summary>
    public async Task<List<CacheFolder>> GetAllCacheFoldersAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFolderPattern(suburb, state);
        var folders = Directory.GetDirectories(_cacheDirectory, pattern)
            .Select(f =>
            {
                var folderName = Path.GetFileName(f);
                var timestamp = LocationHelper.ParseTimestampFromFilename(folderName);
                return new { Folder = f, FolderName = folderName, Timestamp = timestamp };
            })
            .Where(x => x.Timestamp.HasValue)
            .Select(x => new { x.Folder, x.FolderName, Timestamp = x.Timestamp!.Value })
            .OrderBy(x => x.Timestamp) // Oldest first
            .ToList();

        var result = new List<CacheFolder>();
        
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Folder))
                continue;
            
            var metadata = await LoadMetadataAsync(folder.Folder, cancellationToken);
            if (metadata == null)
                continue;
            
            // Check which data types are available in this folder
            var availableDataTypes = new List<CachedDataType>();
            if (CacheHelper.IsCacheFolderCompleteForDataType(folder.Folder, CachedDataType.Radar, _configuration))
            {
                availableDataTypes.Add(CachedDataType.Radar);
            }
            
            if (availableDataTypes.Count > 0)
            {
                result.Add(new CacheFolder
                {
                    FolderName = folder.FolderName,
                    CacheTimestamp = folder.Timestamp,
                    ObservationTime = metadata.ObservationTime,
                    AvailableDataTypes = availableDataTypes,
                    IsComplete = true
                });
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Gets frames from a specific cache folder and data type
    /// </summary>
    public async Task<List<CachedFrame>> GetFramesFromCacheFolderAsync(
        string suburb,
        string state,
        string cacheFolderName,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var folderPath = Path.Combine(_cacheDirectory, cacheFolderName);
        
        if (!Directory.Exists(folderPath))
        {
            return new List<CachedFrame>();
        }
        
        var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, dataType);
        var frames = new List<CachedFrame>();
        
        // Load frame metadata from frames.json if available
        var framesMetadata = await LoadFramesMetadataAsync(folderPath, dataType, cancellationToken);
        var metadataDict = framesMetadata.ToDictionary(f => f.FrameIndex, f => f.MinutesAgo);
        
        // Load frames from data type subfolder
        for (int i = 0; i < frameCount; i++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(folderPath, dataType, i);
            if (File.Exists(framePath))
            {
                if (dataType == CachedDataType.Radar)
                {
                    var minutesAgo = metadataDict.ContainsKey(i) 
                        ? metadataDict[i] 
                        : 40 - (i * 5);
                    
                    frames.Add(new RadarFrame
                    {
                        FrameIndex = i,
                        ImagePath = framePath,
                        MinutesAgo = minutesAgo
                    });
                }
                else
                {
                    // For future data types, create base CachedFrame
                    frames.Add(new CachedFrame
                    {
                        FrameIndex = i,
                        ImagePath = framePath,
                        DataType = dataType
                    });
                }
            }
        }
        
        return frames;
    }

    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    public string GetCacheDirectory() => _cacheDirectory;
    
    /// <summary>
    /// Cleans up incomplete cache folders from previous crashes or restarts
    /// </summary>
    public int CleanupIncompleteCacheFolders()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return 0;
        }
        
        var deletedCount = 0;
        
        try
        {
            // Get all cache folders (they match the pattern LocationKey_Timestamp)
            var folders = Directory.GetDirectories(_cacheDirectory)
                .Where(f => !Path.GetFileName(f).StartsWith("debug", StringComparison.OrdinalIgnoreCase)) // Exclude debug folder
                .ToList();
            
            foreach (var folder in folders)
            {
                try
                {
                    // Check if folder is incomplete (check for radar data type)
                    if (!CacheHelper.IsCacheFolderCompleteForDataType(folder, CachedDataType.Radar, _configuration))
                    {
                        _logger.LogInformation("Found incomplete cache folder from previous session, deleting: {Folder}", Path.GetFileName(folder));
                        Directory.Delete(folder, recursive: true);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check or delete incomplete cache folder: {Folder}", folder);
                }
            }
            
            if (deletedCount > 0)
            {
                _logger.LogInformation("Startup cleanup completed: deleted {Count} incomplete cache folder(s)", deletedCount);
            }
            else
            {
                _logger.LogDebug("Startup cleanup: no incomplete cache folders found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup cleanup of incomplete cache folders");
        }
        
        return deletedCount;
    }
    
    public bool IsLocationUpdating(string locationKey)
    {
        return _activeCacheFolders.ContainsKey(locationKey);
    }
    
    public string? GetActiveCacheFolder(string locationKey)
    {
        return _activeCacheFolders.TryGetValue(locationKey, out var folder) ? folder : null;
    }
    
    public void SetActiveCacheFolder(string locationKey, string cacheFolderPath)
    {
        _activeCacheFolders[locationKey] = cacheFolderPath;
        _logger.LogDebug("Tracking active cache folder: {Folder} for location: {Location}", cacheFolderPath, locationKey);
    }
    
    public void ClearActiveCacheFolder(string locationKey)
    {
        if (_activeCacheFolders.TryRemove(locationKey, out var folder))
        {
            _logger.LogDebug("Cleared active cache folder tracking: {Folder} for location: {Location}", folder, locationKey);
        }
    }
    
    public string CreateCacheFolder(string suburb, string state, string timestamp)
    {
        var cacheFolderPath = FilePathHelper.GetCacheFolderPath(_cacheDirectory, suburb, state, timestamp);
        Directory.CreateDirectory(cacheFolderPath);
        
        // Create radar subfolder structure (for future: create other data type folders as needed)
        var radarFolder = FilePathHelper.GetDataTypeFolderPath(cacheFolderPath, CachedDataType.Radar);
        Directory.CreateDirectory(radarFolder);
        
        _logger.LogDebug("Created cache folder: {Folder}", cacheFolderPath);
        return cacheFolderPath;
    }
    
    public bool TryDeleteIncompleteCacheFolder(string cacheFolderPath)
    {
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return false;
        }
        
        try
        {
            // Check if folder is incomplete for radar data type
            if (!CacheHelper.IsCacheFolderCompleteForDataType(cacheFolderPath, CachedDataType.Radar, _configuration))
            {
                Directory.Delete(cacheFolderPath, recursive: true);
                _logger.LogDebug("Deleted incomplete cache folder: {Folder}", cacheFolderPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete incomplete cache folder: {Folder}", cacheFolderPath);
        }
        
        return false;
    }
    
    public bool TryDeleteEmptyCacheFolder(string cacheFolderPath)
    {
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return false;
        }
        
        try
        {
            var files = Directory.GetFiles(cacheFolderPath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Directory.Delete(cacheFolderPath, recursive: true);
                _logger.LogDebug("Deleted empty cache folder: {Folder}", cacheFolderPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete empty cache folder: {Folder}", cacheFolderPath);
        }
        
        return false;
    }

    public async Task<CacheUpdateStatus> GetCacheStatusAsync(
        string suburb, 
        string state, 
        CachedDataType dataType,
        int cacheExpirationMinutes,
        int cacheManagementCheckIntervalMinutes,
        CancellationToken cancellationToken = default)
    {
        var status = new CacheUpdateStatus();
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        
        // Check if an update is already in progress
        var isUpdating = IsLocationUpdating(locationKey);
        var excludeFolder = GetActiveCacheFolder(locationKey);
        var (cacheFolderPath, cachedMetadata) = await GetCachedScreenshotWithMetadataAsync(suburb, state, dataType, excludeFolder, cancellationToken);
        
        status.CacheExists = !string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath);
        
        if (cachedMetadata != null)
        {
            status.CacheIsValid = IsCacheValid(cachedMetadata);
            status.CacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(cacheExpirationMinutes);
        }
        else
        {
            status.CacheIsValid = false;
        }
        
        // Set update status - if an update is in progress, UpdateTriggered would be false but Message indicates it
        // For GetCacheStatusAsync, we don't trigger updates, but we can indicate if one is in progress
        status.UpdateTriggered = false; // This method doesn't trigger updates
        if (isUpdating)
        {
            status.Message = "Cache update already in progress";
            // Update in progress - estimate completion in ~2 minutes
            status.NextUpdateTime = DateTime.UtcNow.AddMinutes(2);
        }
        else if (status.CacheIsValid && status.CacheExpiresAt.HasValue)
        {
            // Cache is valid - next update will be when cache expires
            status.Message = "Cache is valid, no update needed";
            status.NextUpdateTime = status.CacheExpiresAt.Value;
        }
        else
        {
            // Cache is invalid/missing - next update would be after background service check
            status.Message = status.CacheExists ? "Cache is stale" : "No cache exists";
            var now = DateTime.UtcNow;
            var minutesUntilNextCheck = cacheManagementCheckIntervalMinutes - (now.Minute % cacheManagementCheckIntervalMinutes);
            status.NextUpdateTime = now.AddMinutes(minutesUntilNextCheck);
        }
        
        return status;
    }
}

