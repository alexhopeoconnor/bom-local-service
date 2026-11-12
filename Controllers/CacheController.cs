using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace BomLocalService.Controllers;

[ApiController]
[Route("api/cache")]
public class CacheController : ControllerBase
{
    private readonly ICacheService _cacheService;
    private readonly IBomRadarService _bomRadarService;
    private readonly ICacheRefreshQueue _refreshQueue;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ICacheService cacheService,
        IBomRadarService bomRadarService,
        ICacheRefreshQueue refreshQueue,
        ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _bomRadarService = bomRadarService;
        _refreshQueue = refreshQueue;
        _logger = logger;
    }

    [HttpGet("{suburb}/{state}/range")]
    public async Task<ActionResult<CacheRange>> GetCacheRange(
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
            var result = await _bomRadarService.GetCacheRangeAsync(location!, cancellationToken);
            if (result.TotalCacheFolders == 0)
            {
                var errorResponse = ApiErrorResponseBuilder.CacheNotFound(location!.Suburb, location.State);
                errorResponse.Message = "No cached data found for this location.";
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
            _logger.LogError(ex, "Error getting cache range for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting cache range",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
    }

    [HttpPost("{suburb}/{state}/refresh")]
    public async Task<ActionResult<CacheUpdateStatus>> RefreshCache(
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
            var status = await _bomRadarService.TriggerCacheUpdateAsync(
                location!,
                RefreshRequestSource.Api,
                cancellationToken);

            if (status.RejectionCode == RefreshRequestResultCode.RejectedQueueFull.ToString())
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, status);
            }

            if (status.RejectionCode is nameof(RefreshRequestResultCode.RejectedShuttingDown))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, status);
            }

            if (status.RejectionCode is nameof(RefreshRequestResultCode.RejectedConflict))
            {
                return StatusCode(StatusCodes.Status409Conflict, status);
            }

            if (status.RejectionCode is nameof(RefreshRequestResultCode.RejectedBackoff))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, status);
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering cache update for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.CacheUpdateFailed(
                location.Suburb,
                location.State,
                "Failed to trigger cache update",
                ex);
            return StatusCode(500, errorResponse);
        }
    }

    [HttpGet("{suburb}/{state}/status")]
    public async Task<ActionResult<CacheUpdateStatus>> GetStatus(
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
            return Ok(await _bomRadarService.GetCacheStatusAsync(location!, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache status for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting cache status",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
    }

    [HttpDelete("{suburb}/{state}")]
    public async Task<ActionResult> DeleteCache(
        string suburb,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetLocation(suburb, state, out var location, out var error))
        {
            return error!;
        }

        if (!_refreshQueue.TryBeginDelete(location!, out var conflict))
        {
            var errorResponse = ApiErrorResponseBuilder.ValidationError(
                conflict ?? "The location cannot be deleted while a capture is active",
                "location");
            errorResponse.ErrorCode = "CACHE_DELETION_CONFLICT";
            errorResponse.Message = "A capture is queued or running for this location. Wait for it to finish or retry later.";
            return Conflict(errorResponse);
        }

        try
        {
            var deleted = await _cacheService.DeleteCachedLocationAsync(location!, cancellationToken);
            if (deleted)
            {
                return Ok(new { message = $"Cache deleted for {location!.Suburb}, {location.State}" });
            }

            var errorResponse = ApiErrorResponseBuilder.NotFound(
                "Cache",
                $"{location!.Suburb}, {location.State}",
                "No cached data exists for this location.");
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return NotFound(errorResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location for suburb: {Suburb}, state: {State}", location!.Suburb, location.State);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while deleting cached location",
                ex);
            errorResponse.Details!["location"] = new { suburb = location.Suburb, state = location.State };
            return StatusCode(500, errorResponse);
        }
        finally
        {
            _refreshQueue.EndDelete(location!);
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
}
