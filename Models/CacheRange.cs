namespace BomLocalService.Models;

/// <summary>
/// Information about the available cache range for a location.
/// Existing capture-folder fields retain their meaning; observation bounds are additive.
/// </summary>
public class CacheRange
{
    /// <summary>
    /// Information about the oldest available cache folder by capture time.
    /// </summary>
    public CacheFolder? OldestCache { get; set; }

    /// <summary>
    /// Information about the newest available cache folder by capture time.
    /// </summary>
    public CacheFolder? NewestCache { get; set; }

    /// <summary>
    /// Total number of complete cache folders available.
    /// </summary>
    public int TotalCacheFolders { get; set; }

    /// <summary>
    /// The time span between oldest and newest capture folders (in minutes).
    /// Null if there are fewer than 2 cache folders.
    /// </summary>
    public int? TimeSpanMinutes { get; set; }

    /// <summary>
    /// Oldest radar observation represented by a retained snapshot (UTC).
    /// </summary>
    public DateTime? OldestObservationTime { get; set; }

    /// <summary>
    /// Newest radar observation represented by a retained snapshot (UTC).
    /// </summary>
    public DateTime? NewestObservationTime { get; set; }
}
