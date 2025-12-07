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
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ICacheService cacheService, 
        IBomRadarService bomRadarService,
        ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _bomRadarService = bomRadarService;
        _logger = logger;
    }

    /// <summary>
    /// Get information about the available cache range for a location (oldest and newest cache folders).
    /// Helps clients understand what historical data is available before requesting extended timespans.
    /// </summary>
    [HttpGet("{suburb}/{state}/range")]
    public async Task<ActionResult<CacheRange>> GetCacheRange(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var result = await _bomRadarService.GetCacheRangeAsync(suburb, state, cancellationToken);
            
            if (result.TotalCacheFolders == 0)
            {
                return NotFound(new { error = "No cached data found for this location." });
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache range for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting cache range", message = ex.Message });
        }
    }

    /// <summary>
    /// Manually trigger a cache refresh for a location. Returns status of the update operation.
    /// </summary>
    [HttpPost("{suburb}/{state}/refresh")]
    public async Task<ActionResult<CacheUpdateStatus>> RefreshCache(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var status = await _bomRadarService.TriggerCacheUpdateAsync(suburb, state, cancellationToken);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering cache update for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while triggering cache update", message = ex.Message });
        }
    }

    /// <summary>
    /// Delete cached data for a location.
    /// </summary>
    [HttpDelete("{suburb}/{state}")]
    public async Task<ActionResult> DeleteCache(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var deleted = await _bomRadarService.DeleteCachedLocationAsync(suburb, state, cancellationToken);
            
            if (deleted)
            {
                return Ok(new { message = $"Cache deleted for {suburb}, {state}" });
            }
            else
            {
                return NotFound(new { error = $"No cached data found for {suburb}, {state}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while deleting cached location", message = ex.Message });
        }
    }
}
