namespace BomLocalService.Models;

/// <summary>
/// Response containing radar frames from multiple cache folders (historical data).
/// Used when requesting radar data across a time range rather than just the most recent frames.
/// Radar frames can be joined across cache folders because they represent a historical time series.
/// </summary>
public class RadarTimeSeriesResponse
{
    /// <summary>
    /// List of cache folders, each containing radar frames from a single capture session.
    /// Ordered chronologically (oldest first).
    /// </summary>
    public List<RadarCacheFolderFrames> CacheFolders { get; set; } = new();
    
    /// <summary>
    /// The start time of the requested range (UTC).
    /// </summary>
    public DateTime? StartTime { get; set; }
    
    /// <summary>
    /// The end time of the requested range (UTC).
    /// </summary>
    public DateTime? EndTime { get; set; }
    
    /// <summary>
    /// Total number of frames across all cache folders.
    /// </summary>
    public int TotalFrames { get; set; }
}

/// <summary>
/// A cache folder with its associated radar frames.
/// Represents all frames from a single radar capture session (from the radar subfolder).
/// </summary>
public class RadarCacheFolderFrames
{
    /// <summary>
    /// The cache folder name this frameset came from.
    /// </summary>
    public string CacheFolderName { get; set; } = string.Empty;
    
    /// <summary>
    /// The timestamp when this cache folder was created (UTC).
    /// </summary>
    public DateTime CacheTimestamp { get; set; }
    
    /// <summary>
    /// The observation time from metadata (UTC).
    /// </summary>
    public DateTime ObservationTime { get; set; }
    
    /// <summary>
    /// All radar frames from this cache folder's radar subfolder (typically 7 frames, 0-6).
    /// Frame URLs are already generated and ready to use.
    /// </summary>
    public List<RadarFrame> Frames { get; set; } = new();
}
