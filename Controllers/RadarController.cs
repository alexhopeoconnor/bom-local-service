using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace BomLocalService.Controllers;

[ApiController]
[Route("api/radar")]
public class RadarController : ControllerBase
{
    private readonly IBomRadarService _bomRadarService;
    private readonly ICacheService _cacheService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RadarController> _logger;

    public RadarController(
        IBomRadarService bomRadarService, 
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<RadarController> logger)
    {
        _bomRadarService = bomRadarService;
        _cacheService = cacheService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get radar data for a location. Returns JSON with all frames and their URLs. Triggers background update if cache is stale.
    /// </summary>
    [HttpGet("{suburb}/{state}")]
    public async Task<ActionResult<RadarResponse>> GetRadar(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            // Check if cache needs updating and trigger if needed (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bomRadarService.TriggerCacheUpdateAsync(suburb, state, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background cache update failed for {Suburb}, {State}", suburb, state);
                }
            });

            // Return cached radar data if available (no waiting)
            var result = await _bomRadarService.GetCachedRadarAsync(suburb, state, cancellationToken);
            
            if (result == null || result.Frames.Count == 0)
            {
                // Get cache status to include in 404 response
                // Use cache service directly since this is cache management, not radar-specific
                var cacheExpirationMinutes = (int)_configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
                var cacheManagementCheckIntervalMinutes = _configuration.GetValue<int>("CacheManagement:CheckIntervalMinutes", 5);
                var cacheStatus = await _cacheService.GetCacheStatusAsync(
                    suburb, 
                    state, 
                    CachedDataType.Radar,
                    cacheExpirationMinutes,
                    cacheManagementCheckIntervalMinutes,
                    cancellationToken);
                
                return NotFound(new { 
                    error = "Screenshots not found in cache. Cache update has been triggered in background. Please retry in a few moments.",
                    retryAfter = 30, // seconds
                    refreshEndpoint = $"/api/cache/{suburb}/{state}/refresh",
                    updateTriggered = cacheStatus.UpdateTriggered,
                    cacheExists = cacheStatus.CacheExists,
                    cacheIsValid = cacheStatus.CacheIsValid,
                    cacheExpiresAt = cacheStatus.CacheExpiresAt,
                    nextUpdateTime = cacheStatus.NextUpdateTime,
                    message = cacheStatus.Message
                });
            }
            
            // Generate URLs for frames if not already set
            if (result.Frames.Any(f => string.IsNullOrEmpty(f.ImageUrl)))
            {
                foreach (var frame in result.Frames)
                {
                    frame.ImageUrl = $"/api/radar/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/frame/{frame.FrameIndex}";
                }
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached radar screenshots for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting the radar screenshots", message = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific frame image for a location.
    /// </summary>
    [HttpGet("{suburb}/{state}/frame/{frameIndex}")]
    public async Task<ActionResult> GetFrame(string suburb, string state, int frameIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }
            
            RadarFrame? frame;
            
            // Support cacheFolder query parameter for historical data
            var cacheFolder = Request.Query["cacheFolder"].FirstOrDefault();
            if (!string.IsNullOrEmpty(cacheFolder))
            {
                frame = await _bomRadarService.GetFrameFromCacheFolderAsync(suburb, state, cacheFolder, frameIndex, cancellationToken);
            }
            else
            {
                frame = await _bomRadarService.GetCachedFrameAsync(suburb, state, frameIndex, cancellationToken);
            }
            
            if (frame == null || !System.IO.File.Exists(frame.ImagePath))
            {
                return NotFound(new { error = $"Frame {frameIndex} not found for {suburb}, {state}" });
            }
            
            var imageBytes = await System.IO.File.ReadAllBytesAsync(frame.ImagePath, cancellationToken);
            return File(imageBytes, "image/png", $"frame_{frameIndex}.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame {FrameIndex} for suburb: {Suburb}, state: {State}", frameIndex, suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting the frame", message = ex.Message });
        }
    }

    /// <summary>
    /// Get metadata about the cached radar data for a location (observation time, forecast time, etc.)
    /// </summary>
    [HttpGet("{suburb}/{state}/metadata")]
    public async Task<ActionResult<LastUpdatedInfo>> GetMetadata(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var result = await _bomRadarService.GetLastUpdatedInfoAsync(suburb, state, cancellationToken);
            
            if (result == null)
            {
                return NotFound(new { error = "No cached data found. Use POST /api/cache/{suburb}/{state}/refresh to trigger cache update." });
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting metadata", message = ex.Message });
        }
    }

    /// <summary>
    /// Get historical radar data (frames from multiple cache folders) between specified timestamps.
    /// Radar frames can be joined across cache folders because they represent a historical time series.
    /// </summary>
    [HttpGet("{suburb}/{state}/timeseries")]
    public async Task<ActionResult<RadarTimeSeriesResponse>> GetRadarTimeSeries(
        string suburb, 
        string state, 
        [FromQuery] string? startTime = null, 
        [FromQuery] string? endTime = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            DateTime? start = null;
            DateTime? end = null;
            
            if (!string.IsNullOrEmpty(startTime))
            {
                if (!DateTime.TryParse(startTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedStart))
                {
                    return BadRequest(new { error = "Invalid startTime format. Use ISO 8601 format (e.g., 2025-12-07T00:00:00Z)" });
                }
                start = parsedStart.ToUniversalTime();
            }
            
            if (!string.IsNullOrEmpty(endTime))
            {
                if (!DateTime.TryParse(endTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedEnd))
                {
                    return BadRequest(new { error = "Invalid endTime format. Use ISO 8601 format (e.g., 2025-12-07T12:00:00Z)" });
                }
                end = parsedEnd.ToUniversalTime();
            }
            
            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                return BadRequest(new { error = "startTime must be before or equal to endTime" });
            }

            var result = await _bomRadarService.GetRadarTimeSeriesAsync(suburb, state, start, end, cancellationToken);
            
            if (result.CacheFolders.Count == 0)
            {
                return NotFound(new { error = "No historical data found for the specified time range." });
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting radar time series for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting radar time series", message = ex.Message });
        }
    }
}

