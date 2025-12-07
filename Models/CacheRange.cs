namespace BomLocalService.Models;

/// <summary>
/// Information about the available cache range for a location.
/// Provides oldest and newest cache folder information to help clients understand available historical data.
/// </summary>
public class CacheRange
{
    /// <summary>
    /// Information about the oldest available cache folder.
    /// </summary>
    public CacheFolder? OldestCache { get; set; }
    
    /// <summary>
    /// Information about the newest available cache folder.
    /// </summary>
    public CacheFolder? NewestCache { get; set; }
    
    /// <summary>
    /// Total number of complete cache folders available.
    /// </summary>
    public int TotalCacheFolders { get; set; }
    
    /// <summary>
    /// The time span between oldest and newest cache (in minutes).
    /// Null if there are fewer than 2 cache folders.
    /// </summary>
    public int? TimeSpanMinutes { get; set; }
}
