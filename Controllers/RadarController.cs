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

    [HttpGet("{suburb}/{state}")]
    public async Task<ActionResult<RadarResponse>> GetRadar(
        string suburb,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetLocation(suburb, state, out var location, out var error))
        {
            return error!;
        }

        try
        {
            var triggerStatus = await _bomRadarService.TriggerCacheUpdateAsync(
                location!,
                RefreshRequestSource.Api,
                cancellationToken);

            var result = await _bomRadarService.GetCachedRadarAsync(location!, cancellationToken);
            if (result == null || result.Frames.Count == 0)
            {
                var status = await _bomRadarService.GetCacheStatusAsync(location!, cancellationToken);
                var errorResponse = ApiErrorResponseBuilder.CacheNotFound(
                    location!.Suburb,
                    location.State,
                    status,
                    status.UpdateTriggered || triggerStatus.UpdateTriggered);

                if (status.UpdateFailed)
                {
                    errorResponse.Message = $"No cached data found. Previous update attempt failed: {status.Error ?? "Unknown error"}. Retrying is bounded by the retry schedule.";
                    errorResponse.Details!["previousUpdateFailed"] = true;
                    if (status.Error != null)
                    {
                        errorResponse.Details["previousError"] = status.Error;
                    }
                    if (status.ErrorCode != null)
                    {
                        errorResponse.Details["previousErrorCode"] = status.ErrorCode;
                    }
                    if (status.NextRetryAt.HasValue)
                    {
                        errorResponse.Details["nextRetryAt"] = status.NextRetryAt.Value;
                    }
                }
                else if (status.IsUpdating)
                {
                    errorResponse.Message = "No cached data found. Cache update is currently in progress. Please retry shortly.";
                }
                else if (!status.CacheExists)
                {
                    errorResponse.Message = "No cached data found for this location (fresh start). Cache update has been triggered in background. Please retry in a few moments.";
                }

                if (!string.IsNullOrEmpty(triggerStatus.RejectionCode))
                {
                    errorResponse.Details!["refreshRejected"] = triggerStatus.RejectionCode;
                    if (triggerStatus.RetryAfterSeconds.HasValue)
                    {
                        errorResponse.Details["retryAfterSeconds"] = triggerStatus.RetryAfterSeconds.Value;
                    }
                }

                return NotFound(errorResponse);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached radar screenshots for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting the radar screenshots",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
    }

    [HttpGet("{suburb}/{state}/frame/{frameIndex}")]
    public async Task<ActionResult> GetFrame(
        string suburb,
        string state,
        int frameIndex,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetLocation(suburb, state, out var location, out var error))
        {
            return error!;
        }

        var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, CachedDataType.Radar);
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            return BadRequest(ApiErrorResponseBuilder.ValidationError(
                $"Frame index must be between 0 and {frameCount - 1}, got {frameIndex}",
                "frameIndex"));
        }

        var cacheFolder = Request.Query["cacheFolder"].FirstOrDefault();
        if (!string.IsNullOrEmpty(cacheFolder) &&
            !LocationHelper.TryParseCacheFolderName(cacheFolder, out _))
        {
            return BadRequest(ApiErrorResponseBuilder.ValidationError(
                "cacheFolder must be a recognised cache folder basename without path separators",
                "cacheFolder"));
        }

        try
        {
            RadarFrame? frame;
            if (!string.IsNullOrEmpty(cacheFolder))
            {
                if (!LocationHelper.TryParseCacheFolderName(cacheFolder, out var folderLocation) ||
                    !folderLocation!.Location.Equals(location!))
                {
                    return NotFound(ApiErrorResponseBuilder.NotFound(
                        "Frame",
                        $"Frame {frameIndex} for {location!.Suburb}, {location.State}",
                        "The cache folder does not belong to the requested location."));
                }

                frame = await _bomRadarService.GetFrameFromCacheFolderAsync(
                    location!,
                    cacheFolder,
                    frameIndex,
                    cancellationToken);
            }
            else
            {
                frame = await _bomRadarService.GetCachedFrameAsync(location!, frameIndex, cancellationToken);
            }

            if (frame == null || !System.IO.File.Exists(frame.ImagePath))
            {
                var errorResponse = ApiErrorResponseBuilder.NotFound(
                    "Frame",
                    $"Frame {frameIndex} for {location!.Suburb}, {location.State}",
                    "The frame may not exist yet. Try refreshing the cache or checking if cache update is in progress.");
                errorResponse.Details!["frameIndex"] = frameIndex;
                errorResponse.Details["location"] = new { suburb = location.Suburb, state = location.State };
                if (!string.IsNullOrEmpty(cacheFolder))
                {
                    errorResponse.Details["cacheFolder"] = cacheFolder;
                }

                return NotFound(errorResponse);
            }

            var imageBytes = await System.IO.File.ReadAllBytesAsync(frame.ImagePath, cancellationToken);
            return File(imageBytes, "image/png", $"frame_{frameIndex}.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame {FrameIndex} for suburb: {Suburb}, state: {State}", frameIndex, location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting the frame",
                ex);
            errorResponse.Details!["frameIndex"] = frameIndex;
            errorResponse.Details["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
    }

    [HttpGet("{suburb}/{state}/metadata")]
    public async Task<ActionResult<LastUpdatedInfo>> GetMetadata(
        string suburb,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetLocation(suburb, state, out var location, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _bomRadarService.GetLastUpdatedInfoAsync(location!, cancellationToken);
            if (result == null)
            {
                var errorResponse = ApiErrorResponseBuilder.NotFound(
                    "Metadata",
                    $"{location!.Suburb}, {location.State}",
                    "Use POST /api/cache/{suburb}/{state}/refresh to trigger cache update.");
                errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
                errorResponse.Suggestions = new Dictionary<string, object>
                {
                    { "action", "refresh_cache" },
                    { "refreshEndpoint", $"/api/cache/{Uri.EscapeDataString(location.Suburb)}/{Uri.EscapeDataString(location.State)}/refresh" }
                };
                return NotFound(errorResponse);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting metadata",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
    }

    [HttpGet("{suburb}/{state}/timeseries")]
    public async Task<ActionResult<RadarTimeSeriesResponse>> GetRadarTimeSeries(
        string suburb,
        string state,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetLocation(suburb, state, out var location, out var error))
        {
            return error!;
        }

        if (!TryParseBound(startTime, "startTime", out var start, out var boundError))
        {
            return BadRequest(boundError);
        }

        if (!TryParseBound(endTime, "endTime", out var end, out boundError))
        {
            return BadRequest(boundError);
        }

        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            return BadRequest(ApiErrorResponseBuilder.ValidationError(
                "startTime must be before or equal to endTime",
                "timeRange"));
        }

        var cacheRetentionHours = _configuration.GetValue<int?>("CacheRetentionHours")
            ?? throw new InvalidOperationException("CacheRetentionHours configuration is required.");
        var configuredMaxHours = _configuration.GetValue<int?>("TimeSeries:MaxTimeRangeHours");
        var maxTimeRangeHours = configuredMaxHours ?? Math.Max(cacheRetentionHours, 24);

        if (start.HasValue && end.HasValue)
        {
            var timeRange = end.Value - start.Value;
            if (timeRange.TotalHours > maxTimeRangeHours)
            {
                var reason = configuredMaxHours.HasValue
                    ? $"configured limit: {configuredMaxHours} hours"
                    : $"cache retention: {cacheRetentionHours} hours";

                return BadRequest(ApiErrorResponseBuilder.TimeRangeError(
                    $"Time range exceeds maximum allowed duration of {maxTimeRangeHours} hours (based on {reason}). Please specify a smaller range.",
                    null,
                    new { start = start.Value, end = end.Value, requestedHours = timeRange.TotalHours }));
            }
        }

        try
        {
            var cacheRange = await _bomRadarService.GetCacheRangeAsync(location!, cancellationToken);
            if (cacheRange.TotalCacheFolders == 0)
            {
                await _bomRadarService.TriggerCacheUpdateAsync(location!, RefreshRequestSource.Api, cancellationToken);
                var status = await _bomRadarService.GetCacheStatusAsync(location!, cancellationToken);
                var errorResponse = ApiErrorResponseBuilder.CacheNotFound(
                    location!.Suburb,
                    location.State,
                    status,
                    status.UpdateTriggered);
                errorResponse.Message = "No cached data found for this location. Cache update has been triggered in background. Please retry in a few moments.";
                errorResponse.Details!["requestedTimeRange"] = new { start, end };
                return NotFound(errorResponse);
            }

            var result = await _bomRadarService.GetRadarTimeSeriesAsync(location!, start, end, cancellationToken);
            if (result.CacheFolders.Count == 0)
            {
                var availableRange = new
                {
                    oldest = cacheRange.OldestCache?.CacheTimestamp,
                    newest = cacheRange.NewestCache?.CacheTimestamp,
                    oldestObservationTime = cacheRange.OldestObservationTime,
                    newestObservationTime = cacheRange.NewestObservationTime,
                    totalCacheFolders = cacheRange.TotalCacheFolders,
                    timeSpanMinutes = cacheRange.TimeSpanMinutes
                };

                return NotFound(ApiErrorResponseBuilder.TimeRangeError(
                    "No historical data found for the specified time range.",
                    availableRange,
                    new { start, end }));
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting radar time series for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting radar time series",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            errorResponse.Details["requestedTimeRange"] = new { startTime, endTime };
            return StatusCode(500, errorResponse);
        }
    }

    private bool TryGetLocation(string suburb, string state, out LocationId? location, out ActionResult? error)
    {
        if (!LocationId.TryCreate(suburb, state, out location, out var message))
        {
            error = BadRequest(ApiErrorResponseBuilder.ValidationError(message!));
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseBound(string? value, string field, out DateTime? bound, out ActionResult? error)
    {
        bound = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            error = new BadRequestObjectResult(ApiErrorResponseBuilder.ValidationError(
                $"Invalid {field} format. Use ISO 8601 format with an explicit or implicit offset (e.g., 2025-12-07T00:00:00Z)",
                field));
            return false;
        }

        bound = parsed.UtcDateTime;
        return true;
    }
}
