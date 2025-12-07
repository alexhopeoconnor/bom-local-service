using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace BomLocalService.Services;

public class BomRadarService : IBomRadarService, IDisposable
{
    private readonly ILogger<BomRadarService> _logger;
    private readonly ICacheService _cacheService;
    private readonly IBrowserService _browserService;
    private readonly IScrapingService _scrapingService;
    private readonly IDebugService _debugService;
    private readonly double _cacheExpirationMinutes;
    private readonly int _cacheManagementCheckIntervalMinutes;
    private readonly ConcurrentDictionary<string, string> _activeCacheFolders = new(); // locationKey -> cacheFolderPath

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
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
        _cacheManagementCheckIntervalMinutes = configuration.GetValue<int>("CacheManagement:CheckIntervalMinutes", 5);
    }

    public async Task<RadarResponse?> GetCachedRadarAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var excludeFolder = _activeCacheFolders.TryGetValue(locationKey, out var activeFolder) ? activeFolder : null;
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, excludeFolder, cancellationToken);
        
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
        var isUpdating = _activeCacheFolders.ContainsKey(locationKey);
        
        return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating, _cacheManagementCheckIntervalMinutes);
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
        var isAlreadyUpdating = _activeCacheFolders.ContainsKey(locationKey);
        var excludeFolder = _activeCacheFolders.TryGetValue(locationKey, out var activeFolder) ? activeFolder : null;
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, excludeFolder, cancellationToken);
        
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
            status.NextUpdateTime = status.CacheExpiresAt ?? DateTime.UtcNow.AddMinutes(_cacheExpirationMinutes);
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
            
            // Calculate next update time
            if (status.CacheExpiresAt.HasValue && status.CacheExpiresAt.Value > DateTime.UtcNow)
            {
                status.NextUpdateTime = status.CacheExpiresAt.Value;
            }
            else
            {
                status.NextUpdateTime = DateTime.UtcNow.AddMinutes(_cacheExpirationMinutes);
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
        var excludeFolder = _activeCacheFolders.TryGetValue(locationKey, out var activeFolder) ? activeFolder : null;
        var isUpdating = !string.IsNullOrEmpty(activeFolder);
        var (cacheFolderPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, excludeFolder, cancellationToken);
        
        // If there's an active update in progress, return existing cache (if available) with IsUpdating=true
        if (isUpdating)
        {
            if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath) && CacheHelper.IsCacheFolderComplete(cacheFolderPath))
            {
                _logger.LogInformation("Cache update in progress for {Suburb}, {State}, returning existing cached data", suburb, state);
                var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var isValid = cachedMetadata != null && _cacheService.IsCacheValid(cachedMetadata);
                var cacheExpiresAt = cachedMetadata != null ? cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes) : (DateTime?)null;
                return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating: true, _cacheManagementCheckIntervalMinutes);
            }
            else
            {
                _logger.LogInformation("Cache update in progress for {Suburb}, {State}, but no existing cache found, waiting for update to complete", suburb, state);
                // Still proceed to acquire semaphore - the update might complete while we wait
            }
        }
        
        if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath) && cachedMetadata != null && CacheHelper.IsCacheFolderComplete(cacheFolderPath))
        {
            var isValid = _cacheService.IsCacheValid(cachedMetadata);
            if (isValid)
            {
                _logger.LogInformation("Returning valid cached screenshots for {Suburb}, {State} (no semaphore needed)", suburb, state);
                var frames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var cacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, cachedMetadata, suburb, state, isValid, cacheExpiresAt, isUpdating: false, _cacheManagementCheckIntervalMinutes);
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
            newCacheFolderPath = FilePathHelper.GetCacheFolderPath(_cacheService.GetCacheDirectory(), suburb, state, timestamp);
            Directory.CreateDirectory(newCacheFolderPath);
            
            // Track this folder as being written to
            _activeCacheFolders[locationKey] = newCacheFolderPath;
            _logger.LogDebug("Tracking cache folder being written to: {Folder} for {Location}", newCacheFolderPath, locationKey);
            
            // Double-check cache after acquiring semaphore (another request might have just created it)
            // Exclude the folder we're about to write to
            var (recheckCacheFolderPath, recheckCachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, newCacheFolderPath, cancellationToken);
            if (!string.IsNullOrEmpty(recheckCacheFolderPath) && Directory.Exists(recheckCacheFolderPath) && recheckCachedMetadata != null && _cacheService.IsCacheValid(recheckCachedMetadata))
            {
                // Remove from tracking since we're not using this folder
                _activeCacheFolders.TryRemove(locationKey, out _);
                
                // Clean up the empty folder we created since we're not using it
                if (!string.IsNullOrEmpty(newCacheFolderPath) && Directory.Exists(newCacheFolderPath))
                {
                    try
                    {
                        // Check if folder is empty (only . and ..)
                        var files = Directory.GetFiles(newCacheFolderPath);
                        if (files.Length == 0)
                        {
                            Directory.Delete(newCacheFolderPath, recursive: true);
                            _logger.LogDebug("Cleaned up empty cache folder that was created but not used: {Folder}", newCacheFolderPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up empty cache folder: {Folder}", newCacheFolderPath);
                    }
                }
                
                _logger.LogInformation("Cache became valid while waiting for semaphore, returning cached screenshots");
                var recheckFrames = await _cacheService.GetCachedFramesAsync(suburb, state, cancellationToken);
                var recheckCacheExpiresAt = recheckCachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                return ResponseBuilder.CreateRadarResponse(recheckCacheFolderPath, recheckFrames, recheckCachedMetadata, suburb, state, cacheIsValid: true, recheckCacheExpiresAt, isUpdating: false, _cacheManagementCheckIntervalMinutes);
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
                _activeCacheFolders.TryRemove(locationKey, out _);
                _logger.LogDebug("Cache folder complete, removed from active tracking: {Folder}", newCacheFolderPath);
                
                // Update result with cache state
                result.IsUpdating = false;
                result.CacheIsValid = true;
                result.CacheExpiresAt = result.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                result.NextUpdateTime = result.CacheExpiresAt;
                
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
            _activeCacheFolders.TryRemove(locationKey, out _);
            
            // Clean up incomplete cache folder if error occurred
            if (!string.IsNullOrEmpty(newCacheFolderPath) && Directory.Exists(newCacheFolderPath))
            {
                try
                {
                    if (!CacheHelper.IsCacheFolderComplete(newCacheFolderPath))
                    {
                        Directory.Delete(newCacheFolderPath, recursive: true);
                        _logger.LogWarning("Cleaned up incomplete cache folder due to error: {Folder}", newCacheFolderPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to clean up incomplete cache folder: {Folder}", newCacheFolderPath);
                }
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
        var excludeFolder = _activeCacheFolders.TryGetValue(locationKey, out var activeFolder) ? activeFolder : null;
        var (_, metadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, excludeFolder, cancellationToken);
        
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

    public void Dispose()
    {
        _browserService?.Dispose();
    }
}
