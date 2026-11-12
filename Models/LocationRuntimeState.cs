namespace BomLocalService.Models;

/// <summary>
/// Persisted per-location operational state. Stored beneath .service so cache
/// cleanup cannot mistake it for an incomplete capture.
/// </summary>
public sealed class LocationRuntimeState
{
    public string Suburb { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessfulCaptureAt { get; set; }
    public DateTime? LatestObservationAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public bool UpdateFailed { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime UpdatedAt { get; set; }
}
