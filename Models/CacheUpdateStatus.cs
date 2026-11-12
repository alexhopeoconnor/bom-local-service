namespace BomLocalService.Models;

/// <summary>
/// Represents the current phase of a cache update operation.
/// </summary>
public enum CacheUpdatePhase
{
    Initializing,
    CapturingFrames,
    Saving
}

/// <summary>
/// Status information about a cache update operation for a location.
/// Existing fields remain compatible; operational fields are additive.
/// </summary>
public class CacheUpdateStatus
{
    public bool UpdateTriggered { get; set; }
    public bool CacheExists { get; set; }
    public bool CacheIsValid { get; set; }
    public DateTime? CacheExpiresAt { get; set; }
    public DateTime? NextUpdateTime { get; set; }
    public string? Message { get; set; }

    /// <summary>True if the most recent capture attempt failed.</summary>
    public bool UpdateFailed { get; set; }

    /// <summary>Bounded human-readable error from the most recent failure.</summary>
    public string? Error { get; set; }

    /// <summary>Stable error code: BROWSER_ERROR, SCRAPING_ERROR, TIMEOUT_ERROR, STORAGE_ERROR.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Timestamp when the last update attempt was made (UTC).</summary>
    public DateTime? LastUpdateAttempt { get; set; }

    /// <summary>Consecutive failed capture attempts since the last success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Earliest time the refresh queue will accept another attempt for this location.</summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>Timestamp of the last successful snapshot publication (UTC).</summary>
    public DateTime? LastSuccessfulCaptureAt { get; set; }

    /// <summary>Newest observation time in the latest complete snapshot (UTC).</summary>
    public DateTime? LatestObservationAt { get; set; }

    /// <summary>True while a capture is queued or running.</summary>
    public bool IsUpdating { get; set; }

    /// <summary>True when this status represents a newly accepted refresh request.</summary>
    public bool UpdateAccepted { get; set; }

    /// <summary>Stable rejection code when a refresh request was not accepted.</summary>
    public string? RejectionCode { get; set; }

    /// <summary>Current queue depth at the time the status was built.</summary>
    public int QueueDepth { get; set; }

    /// <summary>Seconds a client should wait before retrying after a rejected request.</summary>
    public int? RetryAfterSeconds { get; set; }
}
