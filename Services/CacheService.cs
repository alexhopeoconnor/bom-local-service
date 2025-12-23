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
    
    // Progress tracking for cache updates
    private readonly ConcurrentDictionary<string, (DateTime startTime, CacheUpdatePhase phase, int? currentFrame, int? totalFrames)> _updateProgress = new();
    private readonly ConcurrentQueue<double> _recentTotalDurations = new(); // Overall durations in seconds
    private readonly ConcurrentDictionary<CacheUpdatePhase, ConcurrentQueue<double>> _phaseDurations = new(); // Phase -> durations
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _stepDurations = new(); // Step name -> durations
    private readonly object _metricsLock = new();
    private const int MaxSamples = 20;

    public CacheService(ILogger<CacheService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        
        var cacheExpirationMinutesConfig = configuration.GetValue<double?>("CacheExpirationMinutes");
        if (!cacheExpirationMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");
        }
        _cacheExpirationMinutes = cacheExpirationMinutesConfig.Value;
        
        if (_cacheExpirationMinutes <= 0)
        {
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0", nameof(configuration));
        }
        
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
        // Exclude active cache folder (currently being written to) to avoid reading incomplete data
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = GetActiveCacheFolder(locationKey);
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
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
        
        // Exclude active cache folder (currently being written to) to avoid reading incomplete data
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = GetActiveCacheFolder(locationKey);
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath))
        {
            return null;
        }
        
        var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, CachedDataType.Radar, frameIndex);
        if (!File.Exists(framePath))
        {
            return null;
        }
        
        // Load frame metadata to get observation time
        var framesMetadata = await LoadFramesMetadataAsync(cacheFolderPath, CachedDataType.Radar, cancellationToken);
        var frameMetadata = framesMetadata.FirstOrDefault(f => f.FrameIndex == frameIndex);
        var observationTime = frameMetadata?.ObservationTime > DateTime.MinValue.AddYears(1) 
            ? frameMetadata.ObservationTime 
            : (DateTime?)null;
            
        return new RadarFrame
        {
            FrameIndex = frameIndex,
            ImagePath = framePath,
            AbsoluteObservationTime = observationTime
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
                ObservationTime = f.AbsoluteObservationTime ?? throw new InvalidOperationException($"Frame {f.FrameIndex} missing AbsoluteObservationTime")
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
        // Exclude active cache folder (currently being written to) to avoid reading incomplete data
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = GetActiveCacheFolder(locationKey);
        
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
            
            // Skip the folder if it's being excluded (currently being written to)
            if (!string.IsNullOrEmpty(excludeFolder) && Path.GetFullPath(folder.Folder).Equals(Path.GetFullPath(excludeFolder), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping excluded cache folder (being written to): {Folder}", folder.Folder);
                continue;
            }
            
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
        var metadataDict = framesMetadata.Where(f => f.ObservationTime > DateTime.MinValue.AddYears(1))
            .ToDictionary(f => f.FrameIndex, f => f.ObservationTime);
        
        // Load frames from data type subfolder
        for (int i = 0; i < frameCount; i++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(folderPath, dataType, i);
            if (File.Exists(framePath))
            {
                if (dataType == CachedDataType.Radar)
                {
                    var observationTime = metadataDict.ContainsKey(i) ? metadataDict[i] : (DateTime?)null;
                    
                    frames.Add(new RadarFrame
                    {
                        FrameIndex = i,
                        ImagePath = framePath,
                        AbsoluteObservationTime = observationTime
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
        _updateProgress[locationKey] = (DateTime.UtcNow, CacheUpdatePhase.Initializing, null, null);
        _logger.LogDebug("Tracking active cache folder: {Folder} for location: {Location}", cacheFolderPath, locationKey);
    }
    
    public void ClearActiveCacheFolder(string locationKey)
    {
        if (_activeCacheFolders.TryRemove(locationKey, out var folder))
        {
            RecordUpdateComplete(locationKey);
            _updateProgress.TryRemove(locationKey, out _);
            _logger.LogDebug("Cleared active cache folder tracking: {Folder} for location: {Location}", folder, locationKey);
        }
    }
    
    /// <summary>
    /// Records progress update for a cache update operation.
    /// </summary>
    public void RecordUpdateProgress(string locationKey, CacheUpdatePhase phase, int? currentFrame = null, int? totalFrames = null)
    {
        if (_updateProgress.TryGetValue(locationKey, out var existing))
        {
            // Record duration for previous phase if it changed
            var previousPhase = existing.phase;
            if (previousPhase != phase)
            {
                var phaseDuration = (DateTime.UtcNow - existing.startTime).TotalSeconds;
                
                lock (_metricsLock)
                {
                    var durations = _phaseDurations.GetOrAdd(previousPhase, _ => new ConcurrentQueue<double>());
                    durations.Enqueue(phaseDuration);
                    
                    while (durations.Count > MaxSamples)
                    {
                        durations.TryDequeue(out _);
                    }
                }
            }
        }
        
        // Update progress
        var startTime = _updateProgress.TryGetValue(locationKey, out var current) ? current.startTime : DateTime.UtcNow;
        _updateProgress[locationKey] = (startTime, phase, currentFrame, totalFrames);
    }
    
    /// <summary>
    /// Records completion of a cache update and stores metrics.
    /// </summary>
    private void RecordUpdateComplete(string locationKey)
    {
        if (_updateProgress.TryRemove(locationKey, out var progress))
        {
            var totalDuration = (DateTime.UtcNow - progress.startTime).TotalSeconds;
            
            lock (_metricsLock)
            {
                _recentTotalDurations.Enqueue(totalDuration);
                while (_recentTotalDurations.Count > MaxSamples)
                {
                    _recentTotalDurations.TryDequeue(out _);
                }
            }
            
            _logger.LogInformation("Cache update completed in {Duration:F1} seconds for {Location}", totalDuration, locationKey);
        }
    }
    
    /// <summary>
    /// Gets the estimated remaining seconds for an in-progress cache update.
    /// Returns 0 if not updating or no metrics available.
    /// </summary>
    public int GetEstimatedRemainingSeconds(string locationKey)
    {
        if (!_updateProgress.TryGetValue(locationKey, out var progress))
        {
            return 0; // Not updating
        }
        
        var elapsed = (DateTime.UtcNow - progress.startTime).TotalSeconds;
        
        // If we have historical data, use it
        var avgTotal = GetAverageTotalDuration();
        if (avgTotal > 0)
        {
            // Estimate based on phase and progress
            double estimatedRemaining = 0;
            
            switch (progress.phase)
            {
                case CacheUpdatePhase.Initializing:
                    // Estimate: avg total - elapsed (with some buffer)
                    estimatedRemaining = Math.Max(0, avgTotal * 1.1 - elapsed);
                    break;
                    
                case CacheUpdatePhase.CapturingFrames:
                    if (progress.currentFrame.HasValue && progress.totalFrames.HasValue)
                    {
                        // We know exactly where we are: frame X of Y
                        var framesRemaining = progress.totalFrames.Value - progress.currentFrame.Value - 1;
                        var avgFrameDuration = GetAverageFrameDuration();
                        if (avgFrameDuration > 0)
                        {
                            // Time for remaining frames + saving phase
                            estimatedRemaining = (framesRemaining * avgFrameDuration) + GetAveragePhaseDuration(CacheUpdatePhase.Saving);
                        }
                        else
                        {
                            // Fallback: estimate based on progress through total
                            var progressFraction = (progress.currentFrame.Value + 1.0) / progress.totalFrames.Value;
                            estimatedRemaining = Math.Max(0, (avgTotal / progressFraction) - elapsed);
                        }
                    }
                    else
                    {
                        // No frame info - estimate based on elapsed time
                        estimatedRemaining = Math.Max(0, avgTotal - elapsed);
                    }
                    break;
                    
                case CacheUpdatePhase.Saving:
                    // Almost done - just saving phase remaining
                    estimatedRemaining = GetAveragePhaseDuration(CacheUpdatePhase.Saving);
                    if (estimatedRemaining == 0)
                    {
                        estimatedRemaining = 5; // Default 5 seconds for saving
                    }
                    break;
            }
            
            return (int)Math.Ceiling(Math.Max(0, estimatedRemaining));
        }
        
        // No historical data - return 0 to signal fallback
        return 0;
    }
    
    /// <summary>
    /// Gets the average total duration of cache updates from recent metrics.
    /// </summary>
    public double GetAverageTotalDuration()
    {
        lock (_metricsLock)
        {
            if (_recentTotalDurations.Count == 0) return 0;
            var durations = _recentTotalDurations.ToArray();
            Array.Sort(durations);
            // Use median for robustness
            var median = durations.Length % 2 == 0
                ? (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2.0
                : durations[durations.Length / 2];
            return median;
        }
    }
    
    /// <summary>
    /// Gets the average duration per frame based on historical CapturingFrames phase data.
    /// </summary>
    private double GetAverageFrameDuration()
    {
        // Estimate frame duration from CapturingFrames phase duration / frame count
        var avgCapturingDuration = GetAveragePhaseDuration(CacheUpdatePhase.CapturingFrames);
        var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, CachedDataType.Radar);
        return avgCapturingDuration > 0 && frameCount > 0 ? avgCapturingDuration / frameCount : 0;
    }
    
    /// <summary>
    /// Gets the average duration for a specific phase from historical data.
    /// </summary>
    private double GetAveragePhaseDuration(CacheUpdatePhase phase)
    {
        lock (_metricsLock)
        {
            if (!_phaseDurations.TryGetValue(phase, out var durations) || durations.Count == 0)
            {
                return 0;
            }
            var durationsArray = durations.ToArray();
            return durationsArray.Average();
        }
    }
    
    /// <summary>
    /// Records step completion timing for metrics tracking.
    /// </summary>
    public void RecordStepCompletion(string stepName, double durationSeconds)
    {
        lock (_metricsLock)
        {
            var durations = _stepDurations.GetOrAdd(stepName, _ => new ConcurrentQueue<double>());
            durations.Enqueue(durationSeconds);
            
            while (durations.Count > MaxSamples)
            {
                durations.TryDequeue(out _);
            }
        }
    }
    
    /// <summary>
    /// Gets the average duration for a specific step from historical data.
    /// </summary>
    public double GetAverageStepDuration(string stepName)
    {
        lock (_metricsLock)
        {
            if (!_stepDurations.TryGetValue(stepName, out var durations) || durations.Count == 0)
            {
                return 0;
            }
            var durationsArray = durations.ToArray();
            return durationsArray.Average();
        }
    }
    
    /// <summary>
    /// Gets step performance metrics for debugging/logging.
    /// </summary>
    public Dictionary<string, double> GetStepMetrics()
    {
        lock (_metricsLock)
        {
            var metrics = new Dictionary<string, double>();
            foreach (var kvp in _stepDurations)
            {
                if (kvp.Value.Count > 0)
                {
                    var durations = kvp.Value.ToArray();
                    metrics[kvp.Key] = durations.Average();
                }
            }
            return metrics;
        }
    }
    
    /// <summary>
    /// Gets the locationKey from a cacheFolderPath by parsing the folder name.
    /// </summary>
    private string? GetLocationKeyFromCacheFolder(string cacheFolderPath)
    {
        var folderName = Path.GetFileName(cacheFolderPath);
        var location = LocationHelper.ParseLocationFromFilename(folderName);
        if (location.HasValue)
        {
            return LocationHelper.GetLocationKey(location.Value.suburb, location.Value.state);
        }
        return null;
    }
    
    /// <summary>
    /// Records progress update for a cache update operation using cacheFolderPath.
    /// This is useful when locationKey is not directly available (e.g., in ScrapingService).
    /// </summary>
    public void RecordUpdateProgressByFolder(string cacheFolderPath, CacheUpdatePhase phase, int? currentFrame = null, int? totalFrames = null)
    {
        var locationKey = GetLocationKeyFromCacheFolder(cacheFolderPath);
        if (locationKey != null)
        {
            RecordUpdateProgress(locationKey, phase, currentFrame, totalFrames);
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
            // Try metrics-based estimate first
            var remainingSeconds = GetEstimatedRemainingSeconds(locationKey);
            if (remainingSeconds > 0)
            {
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(remainingSeconds);
            }
            else
            {
                // Fallback to calculated estimate
                var estimatedDurationSeconds = CacheHelper.GetEstimatedUpdateDurationSeconds(_configuration, dataType);
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(estimatedDurationSeconds);
            }
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
            status.NextUpdateTime = ResponseBuilder.CalculateNextServiceCheck(cacheManagementCheckIntervalMinutes);
        }
        
        return status;
    }
}

