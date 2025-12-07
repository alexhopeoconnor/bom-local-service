namespace BomLocalService.Models;

/// <summary>
/// Status information about a cache update operation for a location.
/// Returned when manually triggering a cache refresh via the refresh endpoint.
/// </summary>
public class CacheUpdateStatus
{
    /// <summary>
    /// Indicates whether a background cache update was triggered.
    /// True if the cache was missing or stale and an update was initiated.
    /// False if the cache is valid and no update was needed.
    /// </summary>
    public bool UpdateTriggered { get; set; }

    /// <summary>
    /// Indicates whether cached screenshot files exist for this location.
    /// True if PNG screenshot files are found in the cache directory.
    /// False if no cached files exist for this location.
    /// </summary>
    public bool CacheExists { get; set; }

    /// <summary>
    /// Indicates whether the existing cache is still valid (not expired).
    /// Cache is considered valid if the observation time plus expiration buffer (typically 15.5 minutes)
    /// is still in the future. BOM updates observations every 15 minutes, so cache expiration
    /// includes a small buffer to account for timing variations.
    /// True if cache exists and is still valid, false if cache is missing or expired.
    /// </summary>
    public bool CacheIsValid { get; set; }

    /// <summary>
    /// The UTC timestamp when the current cache will expire and should be refreshed.
    /// Calculated as: ObservationTime + CacheExpirationMinutes (typically observation time + 15.5 minutes).
    /// Null if no cache exists or metadata is not available.
    /// </summary>
    public DateTime? CacheExpiresAt { get; set; }

    /// <summary>
    /// The UTC timestamp when the next cache update should occur.
    /// If cache is valid: equals CacheExpiresAt (when current cache expires).
    /// If cache is invalid/missing: equals current time + CacheExpirationMinutes (when background update will complete).
    /// Null if cache is valid and CacheExpiresAt is null.
    /// </summary>
    public DateTime? NextUpdateTime { get; set; }

    /// <summary>
    /// Human-readable message describing the cache status and action taken.
    /// Possible values:
    /// - "Cache is valid, no update needed" - Cache exists and is still fresh
    /// - "Cache is stale, update triggered" - Cache exists but expired, update initiated
    /// - "No cache exists, update triggered" - No cache found, update initiated
    /// - "Cache update already in progress" - An update is currently running
    /// </summary>
    public string? Message { get; set; }
}

