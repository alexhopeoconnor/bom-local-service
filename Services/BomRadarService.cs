using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public class BomRadarService : IBomRadarService, IDisposable
{
    private readonly ILogger<BomRadarService> _logger;
    private readonly ICacheService _cacheService;
    private readonly IBrowserService _browserService;
    private readonly IScrapingService _scrapingService;
    private readonly IDebugService _debugService;
    private readonly IConfiguration _configuration;
    private readonly double _cacheExpirationMinutes;
    private readonly int _cacheManagementCheckIntervalMinutes;
    private readonly int _timeSeriesWarningFolderCount;
    private readonly int _estimatedUpdateDurationSeconds;

    public BomRadarService(
        ILogger<BomRadarService> logger,
        ICacheService cacheService,
        IBrowserService browserService,
        IScrapingService scrapingService,
        IDebugService debugService,
        IConfiguration configuration)
    {
        _logger = logger;
        _cacheService = cacheService;
        _browserService = browserService;
        _scrapingService = scrapingService;
        _debugService = debugService;
        _configuration = configuration;
        
        var cacheExpirationMinutesConfig = configuration.GetValue<double?>("CacheExpirationMinutes");
        if (!cacheExpirationMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");
        }
        _cacheExpirationMinutes = cacheExpirationMinutesConfig.Value;
        
        var cacheManagementCheckIntervalMinutesConfig = configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes");
        if (!cacheManagementCheckIntervalMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
        }
        _cacheManagementCheckIntervalMinutes = cacheManagementCheckIntervalMinutesConfig.Value;
        
        var timeSeriesWarningFolderCountConfig = configuration.GetValue<int?>("TimeSeries:WarningFolderCount");
        if (!timeSeriesWarningFolderCountConfig.HasValue)
        {
            throw new InvalidOperationException("TimeSeries:WarningFolderCount configuration is required. Set it in appsettings.json or via TIMESERIES__WARNINGFOLDERCOUNT environment variable.");
        }
        _timeSeriesWarningFolderCount = timeSeriesWarningFolderCountConfig.Value;
        
        // Calculate estimated cache update duration
        _estimatedUpdateDurationSeconds = CacheHelper.GetEstimatedUpdateDurationSeconds(configuration, CachedDataType.Radar);
        _logger.LogInformation("Estimated cache update duration: {Seconds} seconds", _estimatedUpdateDurationSeconds);
        
        if (_cacheExpirationMinutes <= 0)
        {
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0", nameof(configuration));
        }
        if (_cacheManagementCheckIntervalMinutes <= 0 || _cacheManagementCheckIntervalMinutes > 60)
        {
            throw new ArgumentException("CacheManagement:CheckIntervalMinutes must be between 1 and 60", nameof(configuration));
        }
        if (_timeSeriesWarningFolderCount <= 0)
        {
            throw new ArgumentException("TimeSeries:WarningFolderCount must be greater than 0", nameof(configuration));
        }
        if (_estimatedUpdateDurationSeconds <= 0)
        {
            throw new ArgumentException("Calculated estimated update duration must be greater than 0. Check Screenshot:TileRenderWaitMs, Screenshot:DynamicContentWaitMs, and CachedDataTypes:Radar:FrameCount configuration values.", nameof(configuration));
        }
    }

    public async Task<RadarResponse?> GetCachedRadarAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = _cacheService.GetActiveCacheFolder(locationKey);
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return null;
        }

        var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
        if (frames == null || frames.Count == 0)
        {
            return null;
        }

        // Determine cache state
        var isValid = cachedMetadata != null && _cacheService.IsCacheValid(cachedMetadata);
        var cacheExpiresAt = cachedMetadata != null ? cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes) : (DateTime?)null;
        var isUpdating = _cacheService.IsLocationUpdating(locationKey);
        
        // Use metrics-based estimate if available, otherwise fallback
        var estimatedDuration = _cacheService.GetEstimatedRemainingSeconds(locationKey);
        var durationToUse = estimatedDuration > 0 ? estimatedDuration : _estimatedUpdateDurationSeconds;
        
        return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, _cacheManagementCheckIntervalMinutes, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating, durationToUse);
    }
    
    public async Task<List<RadarFrame>?> GetCachedFramesAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
        
        // Generate URLs for frames
        foreach (var frame in frames)
        {
            frame.ImageUrl = $"/api/radar/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/frame/{frame.FrameIndex}";
        }
        
        return frames.Count > 0 ? frames : null;
    }
    
    public async Task<RadarFrame?> GetCachedFrameAsync(string suburb, string state, int frameIndex, CancellationToken cancellationToken = default)
    {
        return await _cacheService.GetCachedFrameAsync(suburb, state, frameIndex, cancellationToken);
    }

    public async Task<CacheUpdateStatus> TriggerCacheUpdateAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var status = new CacheUpdateStatus();
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        
        // Check if an update is already in progress
        var isAlreadyUpdating = _cacheService.IsLocationUpdating(locationKey);
        var excludeFolder = _cacheService.GetActiveCacheFolder(locationKey);
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
        if (isAlreadyUpdating)
        {
            _logger.LogDebug("Cache update already in progress for {Suburb}, {State}, skipping trigger", suburb, state);
            
            status.CacheExists = !string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath);
            if (cachedMetadata != null)
            {
                status.CacheIsValid = _cacheService.IsCacheValid(cachedMetadata);
                status.CacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
            }
            else
            {
                status.CacheIsValid = false;
            }
            
            status.UpdateTriggered = false;
            status.Message = "Cache update already in progress";
            
            // Use metrics-based estimate if available, otherwise fallback
            var remainingSeconds = _cacheService.GetEstimatedRemainingSeconds(locationKey);
            if (remainingSeconds > 0)
            {
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(remainingSeconds);
            }
            else
            {
                // Fallback to calculated estimate
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(_estimatedUpdateDurationSeconds);
            }
            
            return status;
        }
        
        status.CacheExists = !string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath);
        
        if (cachedMetadata != null)
        {
            status.CacheIsValid = _cacheService.IsCacheValid(cachedMetadata);
            status.CacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        }
        else
        {
            // If no metadata, cache cannot be valid
            status.CacheIsValid = false;
        }

        // Check if we need to update
        bool needsUpdate = !status.CacheExists || !status.CacheIsValid;
        
        if (needsUpdate)
        {
            // Trigger async update (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchAndCacheScreenshotAsync(suburb, state, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during background cache update for {Suburb}, {State}", suburb, state);
                }
            }, cancellationToken);
            
            status.UpdateTriggered = true;
            status.Message = status.CacheExists 
                ? "Cache is stale, update triggered" 
                : "No cache exists, update triggered";
            
            // Try metrics-based estimate first, then fallback to calculated
            var remainingSeconds = _cacheService.GetEstimatedRemainingSeconds(locationKey);
            if (remainingSeconds > 0)
            {
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(remainingSeconds);
            }
            else
            {
                // Fallback to calculated estimate
                status.NextUpdateTime = DateTime.UtcNow.AddSeconds(_estimatedUpdateDurationSeconds);
            }
        }
        else
        {
            status.UpdateTriggered = false;
            status.Message = "Cache is valid, no update needed";
            status.NextUpdateTime = status.CacheExpiresAt;
        }

        return status;
    }

    private async Task<RadarResponse> FetchAndCacheScreenshotAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting radar screenshot for suburb: {Suburb}, state: {State}", suburb, state);

        // Check cache FIRST, before acquiring semaphore (cached requests shouldn't block)
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = _cacheService.GetActiveCacheFolder(locationKey);
        var isUpdating = _cacheService.IsLocationUpdating(locationKey);
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
        // If there's an active update in progress, return existing cache (if available) with IsUpdating=true
        if (isUpdating)
        {
            if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath) && CacheHelper.IsCacheFolderComplete(cacheFolderPath, _configuration))
            {
                _logger.LogInformation("Cache update in progress for {Suburb}, {State}, returning existing cached data", suburb, state);
                var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var isValid = cachedMetadata != null && _cacheService.IsCacheValid(cachedMetadata);
                var cacheExpiresAt = cachedMetadata != null ? cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes) : (DateTime?)null;
                
                // Use metrics-based estimate if available, otherwise fallback
                var estimatedDuration = _cacheService.GetEstimatedRemainingSeconds(locationKey);
                var durationToUse = estimatedDuration > 0 ? estimatedDuration : _estimatedUpdateDurationSeconds;
                
                return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, _cacheManagementCheckIntervalMinutes, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating: true, durationToUse);
            }
            else
            {
                _logger.LogInformation("Cache update in progress for {Suburb}, {State}, but no existing cache found, waiting for update to complete", suburb, state);
                // Still proceed to acquire semaphore - the update might complete while we wait
            }
        }
        
        if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath) && cachedMetadata != null && CacheHelper.IsCacheFolderComplete(cacheFolderPath, _configuration))
        {
            var isValid = _cacheService.IsCacheValid(cachedMetadata);
            if (isValid)
            {
                _logger.LogInformation("Returning valid cached screenshots for {Suburb}, {State} (no semaphore needed)", suburb, state);
                var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var cacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, _cacheManagementCheckIntervalMinutes, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating: false, _estimatedUpdateDurationSeconds);
            }
            else
            {
                var nextUpdate = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                var timeUntilExpiry = nextUpdate - DateTime.UtcNow;
                _logger.LogInformation("Cached screenshots exist but are stale (observation time: {ObservationTime}, expired {TimeAgo} ago), fetching new ones", 
                    cachedMetadata.ObservationTime, -timeUntilExpiry);
            }
        }
        else if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath))
        {
            _logger.LogWarning("Cached folder found but no metadata file exists, fetching new screenshots");
        }
        else
        {
            _logger.LogInformation("No cached screenshots found for {Suburb}, {State}, fetching new ones", suburb, state);
        }

        // Only acquire semaphore if we need to fetch new screenshot
        var semaphore = _browserService.GetSemaphore();
        await semaphore.WaitAsync(cancellationToken);
        
        IBrowserContext? context = null;
        string? debugFolder = null;
        string? requestId = null;
        string? newCacheFolderPath = null;
        try
        {
            // Create cache folder and track it before double-checking
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            newCacheFolderPath = _cacheService.CreateCacheFolder(suburb, state, timestamp);
            
            // Track this folder as being written to
            _cacheService.SetActiveCacheFolder(locationKey, newCacheFolderPath);
            
            // Double-check cache after acquiring semaphore (another request might have just created it)
            // Exclude the folder we're about to write to
            var (recheckCacheFolderPath, recheckCachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, newCacheFolderPath, cancellationToken);
            if (!string.IsNullOrEmpty(recheckCacheFolderPath) && Directory.Exists(recheckCacheFolderPath) && recheckCachedMetadata != null && _cacheService.IsCacheValid(recheckCachedMetadata))
            {
                // Remove from tracking since we're not using this folder
                _cacheService.ClearActiveCacheFolder(locationKey);
                
                // Clean up the empty folder we created since we're not using it
                _cacheService.TryDeleteEmptyCacheFolder(newCacheFolderPath);
                
                _logger.LogInformation("Cache became valid while waiting for semaphore, returning cached screenshots");
                var recheckFrames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var recheckCacheExpiresAt = recheckCachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                return ResponseBuilder.CreateRadarResponse(recheckCacheFolderPath, recheckFrames, _cacheManagementCheckIntervalMinutes, recheckCachedMetadata, suburb, state, cacheIsValid: true, recheckCacheExpiresAt, isUpdating: false);
            }
            
            // Create debug folder only now
            requestId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            debugFolder = _debugService.CreateRequestFolder(requestId);
            
            context = await _browserService.CreateContextAsync();
            var (page, consoleMessages, networkRequests) = await _browserService.CreatePageWithDebugAsync(context, requestId);

            try
            {
                var result = await _scrapingService.ScrapeRadarScreenshotAsync(
                    suburb,
                    state,
                    newCacheFolderPath,
                    debugFolder,
                    page,
                    consoleMessages,
                    networkRequests,
                    cancellationToken);
                
                // Remove from active tracking once complete
                _cacheService.ClearActiveCacheFolder(locationKey);
                
                // Update result with cache state - rebuild response to get correct NextUpdateTime calculation
                var cacheExpiresAt = result.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                var metadata = new LastUpdatedInfo
                {
                    ObservationTime = result.ObservationTime,
                    ForecastTime = result.ForecastTime,
                    WeatherStation = result.WeatherStation,
                    Distance = result.Distance
                };
                result = ResponseBuilder.CreateRadarResponse(
                    cacheFolderPath: newCacheFolderPath,
                    frames: result.Frames,
                    metadata: metadata,
                    suburb: suburb,
                    state: state,
                    cacheIsValid: true,
                    cacheExpiresAt: cacheExpiresAt,
                    isUpdating: false,
                    cacheManagementCheckIntervalMinutes: _cacheManagementCheckIntervalMinutes);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during radar screenshot capture (RequestId: {RequestId})", requestId);
                throw;
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting radar screenshot for suburb: {Suburb}, state: {State} (RequestId: {RequestId})", suburb, state, requestId ?? "unknown");
            throw;
        }
        finally
        {
            // Always remove from active tracking, even on error
            _cacheService.ClearActiveCacheFolder(locationKey);
            
            // Clean up incomplete cache folder if error occurred
            if (!string.IsNullOrEmpty(newCacheFolderPath) && _cacheService.TryDeleteIncompleteCacheFolder(newCacheFolderPath))
            {
                _logger.LogWarning("Cleaned up incomplete cache folder due to error: {Folder}", newCacheFolderPath);
            }
            
            // Clean up empty debug folder if we returned early without scraping
            if (!string.IsNullOrEmpty(debugFolder) && Directory.Exists(debugFolder))
            {
                try
                {
                    var files = Directory.GetFiles(debugFolder, "*", SearchOption.AllDirectories);
                    if (files.Length == 0)
                    {
                        Directory.Delete(debugFolder, recursive: true);
                        _logger.LogDebug("Removed empty debug folder: {DebugFolder}", debugFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up empty debug folder: {DebugFolder}", debugFolder);
                }
            }
            
            semaphore.Release();
        }
    }

    public async Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        // Return cached metadata if available, otherwise return null
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = _cacheService.GetActiveCacheFolder(locationKey);
        var (_, metadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, CachedDataType.Radar, excludeFolder, cancellationToken);
        
        if (metadata != null)
        {
            _logger.LogDebug("Returning cached last updated info for {Suburb}, {State}", suburb, state);
            return metadata;
        }

        _logger.LogDebug("No cached metadata found for {Suburb}, {State}", suburb, state);
        return null;
    }

    public Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        return _cacheService.GetCachedScreenshotPathAsync(suburb, state, cancellationToken);
    }

    public Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        return _cacheService.DeleteCachedLocationAsync(suburb, state, cancellationToken);
    }

    public async Task<CacheRange> GetCacheRangeAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var allFolders = await _cacheService.GetAllCacheFoldersAsync(suburb, state, cancellationToken);
        
        if (allFolders.Count == 0)
        {
            return new CacheRange
            {
                TotalCacheFolders = 0
            };
        }
        
        var oldest = allFolders.First();
        var newest = allFolders.Last();
        
        var timeSpanMinutes = allFolders.Count >= 2
            ? (int?)(newest.CacheTimestamp - oldest.CacheTimestamp).TotalMinutes
            : null;
        
        return new CacheRange
        {
            OldestCache = oldest,
            NewestCache = newest,
            TotalCacheFolders = allFolders.Count,
            TimeSpanMinutes = timeSpanMinutes
        };
    }

    public async Task<RadarTimeSeriesResponse> GetRadarTimeSeriesAsync(
        string suburb, 
        string state, 
        DateTime? startTime, 
        DateTime? endTime, 
        CancellationToken cancellationToken = default)
    {
        var allFolders = await _cacheService.GetAllCacheFoldersAsync(suburb, state, cancellationToken);
        
        // Filter by time range if provided
        var filteredFolders = allFolders.AsEnumerable();
        if (startTime.HasValue)
        {
            filteredFolders = filteredFolders.Where(f => f.CacheTimestamp >= startTime.Value);
        }
        if (endTime.HasValue)
        {
            filteredFolders = filteredFolders.Where(f => f.CacheTimestamp <= endTime.Value);
        }
        
        var filteredFoldersList = filteredFolders.ToList();
        
        // Warn if processing a large number of folders (configurable threshold)
        if (filteredFoldersList.Count > _timeSeriesWarningFolderCount)
        {
            _logger.LogWarning(
                "Processing large number of cache folders ({Count}) for time series request. Suburb: {Suburb}, State: {State}, StartTime: {StartTime}, EndTime: {EndTime}",
                filteredFoldersList.Count, suburb, state, startTime, endTime);
        }
        
        var result = new List<RadarCacheFolderFrames>();
        var encodedSuburb = Uri.EscapeDataString(suburb);
        var encodedState = Uri.EscapeDataString(state);
        
        // Process folders in reverse chronological order (newest first) so we keep frames from most recent cache folders
        // Use a HashSet to track unique absolute observation times we've already seen
        var seenAbsoluteTimes = new HashSet<DateTime>();
        
        foreach (var folderInfo in filteredFoldersList.OrderByDescending(f => f.CacheTimestamp))
        {
            // Load frames from this folder's radar subfolder
            var cachedFrames = await _cacheService.GetFramesFromCacheFolderAsync(
                suburb, 
                state, 
                folderInfo.FolderName, 
                CachedDataType.Radar, 
                cancellationToken);
            
            var radarFrames = cachedFrames.Cast<RadarFrame>()
                .OrderBy(f => f.FrameIndex) // Ensure frames are sorted by index
                .ToList();
            
            if (radarFrames.Count > 0)
            {
                // Generate URLs and filter frames by unique absolute observation times
                var uniqueFrames = new List<RadarFrame>();
                
                foreach (var frame in radarFrames)
                {
                    frame.ImageUrl = $"/api/radar/{encodedSuburb}/{encodedState}/frame/{frame.FrameIndex}?cacheFolder={Uri.EscapeDataString(folderInfo.FolderName)}";
                    
                    // Use absolute observation time directly (already set during capture)
                    // Only include frames with unique absolute observation times
                    // Since we process folders newest-first, this ensures we keep frames from the most recent cache folder
                    if (frame.AbsoluteObservationTime.HasValue && 
                        frame.AbsoluteObservationTime.Value > DateTime.MinValue.AddYears(1) &&
                        !seenAbsoluteTimes.Contains(frame.AbsoluteObservationTime.Value))
                    {
                        seenAbsoluteTimes.Add(frame.AbsoluteObservationTime.Value);
                        uniqueFrames.Add(frame);
                    }
                    else if (!frame.AbsoluteObservationTime.HasValue)
                    {
                        _logger.LogWarning("Skipping frame {FrameIndex} from folder {FolderName}: Missing AbsoluteObservationTime", 
                            frame.FrameIndex, folderInfo.FolderName);
                    }
                }
                
                // Only add folder to result if it has unique frames
                if (uniqueFrames.Count > 0)
                {
                    // Sort frames within folder by absolute observation time (oldest first) for slideshow
                    uniqueFrames = uniqueFrames
                        .OrderBy(f => f.AbsoluteObservationTime ?? DateTime.MaxValue)
                        .ToList();
                    
                    result.Add(new RadarCacheFolderFrames
                    {
                        CacheFolderName = folderInfo.FolderName,
                        CacheTimestamp = folderInfo.CacheTimestamp,
                        ObservationTime = folderInfo.ObservationTime,
                        Frames = uniqueFrames
                    });
                }
            }
        }
        
        // Flatten all frames, sort by absolute observation time, and filter out frames that are too close together
        // Frames should be at least 4 minutes apart, and must be in strict chronological order
        var allFrames = result.SelectMany(r => r.Frames.Select(f => new { Folder = r, Frame = f }))
            .Where(x => x.Frame.AbsoluteObservationTime.HasValue)
            .OrderBy(x => x.Frame.AbsoluteObservationTime!.Value)
            .ToList();
        
        // Filter out frames that are too close together (< 4 minutes) or out of order
        // Track which absolute times passed the filter
        var acceptedAbsoluteTimes = new HashSet<DateTime>();
        DateTime? lastAbsoluteTime = null;
        const int minMinutesBetweenFrames = 4;
        
        foreach (var item in allFrames)
        {
            var absoluteTime = item.Frame.AbsoluteObservationTime!.Value;
            
            // Skip if this frame is older than the previous one (shouldn't happen after sorting, but safety check)
            if (lastAbsoluteTime.HasValue && absoluteTime < lastAbsoluteTime.Value)
            {
                _logger.LogWarning("Skipping frame {FrameIndex} from folder {FolderName}: absolute time {AbsoluteTime} is older than previous {PreviousTime}",
                    item.Frame.FrameIndex, item.Folder.CacheFolderName, absoluteTime, lastAbsoluteTime.Value);
                continue;
            }
            
            // Skip if this frame is too close to the previous one (< 4 minutes)
            if (lastAbsoluteTime.HasValue)
            {
                var timeDiff = absoluteTime - lastAbsoluteTime.Value;
                if (timeDiff.TotalMinutes < minMinutesBetweenFrames)
                {
                    _logger.LogDebug("Skipping frame {FrameIndex} from folder {FolderName}: only {Minutes} minutes after previous frame (minimum {MinMinutes})",
                        item.Frame.FrameIndex, item.Folder.CacheFolderName, timeDiff.TotalMinutes, minMinutesBetweenFrames);
                    continue;
                }
            }
            
            acceptedAbsoluteTimes.Add(absoluteTime);
            lastAbsoluteTime = absoluteTime;
        }
        
        // Prune frames from original folder structure - only keep frames that passed the filter
        // This preserves the original folder order and structure
        foreach (var folder in result)
        {
            folder.Frames = folder.Frames
                .Where(f => f.AbsoluteObservationTime.HasValue && acceptedAbsoluteTimes.Contains(f.AbsoluteObservationTime.Value))
                .OrderBy(f => f.AbsoluteObservationTime!.Value) // Sort within folder by absolute time
                .ToList();
        }
        
        // Remove empty folders and sort folders to ensure chronological order when client flattens
        // To guarantee chronological order when iterating folder-by-folder, we need to ensure
        // that the LAST frame of folder N is before the FIRST frame of folder N+1
        // Since we've already deduplicated and filtered, folders should not have overlapping frames
        // Sort by earliest frame - if folders overlap, the deduplication should have prevented it
        result = result
            .Where(r => r.Frames.Count > 0) // Only include folders that still have frames after filtering
            .OrderBy(r => r.Frames
                .Where(f => f.AbsoluteObservationTime.HasValue)
                .Select(f => f.AbsoluteObservationTime!.Value)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min()) // Sort by earliest frame
            .ToList();
        
        // Validate that folders don't overlap (last frame of N < first frame of N+1)
        for (int i = 0; i < result.Count - 1; i++)
        {
            var currentFolder = result[i];
            var nextFolder = result[i + 1];
            
            var currentLast = currentFolder.Frames
                .Where(f => f.AbsoluteObservationTime.HasValue)
                .Select(f => f.AbsoluteObservationTime!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            
            var nextFirst = nextFolder.Frames
                .Where(f => f.AbsoluteObservationTime.HasValue)
                .Select(f => f.AbsoluteObservationTime!.Value)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();
            
            if (currentLast >= nextFirst)
            {
                _logger.LogWarning("Folder overlap detected: {CurrentFolder} ends at {CurrentLast}, {NextFolder} starts at {NextFirst}. This may cause out-of-order frames when client flattens.",
                    currentFolder.CacheFolderName, currentLast, nextFolder.CacheFolderName, nextFirst);
            }
        }
        
        var totalFrames = result.Sum(cf => cf.Frames.Count);
        
        return new RadarTimeSeriesResponse
        {
            CacheFolders = result,
            StartTime = startTime,
            EndTime = endTime,
            TotalFrames = totalFrames
        };
    }

    public async Task<RadarFrame?> GetFrameFromCacheFolderAsync(
        string suburb, 
        string state, 
        string cacheFolderName,
        int frameIndex, 
        CancellationToken cancellationToken = default)
    {
        var cachedFrames = await _cacheService.GetFramesFromCacheFolderAsync(
            suburb, 
            state, 
            cacheFolderName, 
            CachedDataType.Radar, 
            cancellationToken);
        
        var radarFrame = cachedFrames.Cast<RadarFrame>().FirstOrDefault(f => f.FrameIndex == frameIndex);
        
        if (radarFrame != null)
        {
            // Generate URL
            radarFrame.ImageUrl = $"/api/radar/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/frame/{frameIndex}?cacheFolder={Uri.EscapeDataString(cacheFolderName)}";
        }
        
        return radarFrame;
    }


    public void Dispose()
    {
        _browserService?.Dispose();
    }
}
