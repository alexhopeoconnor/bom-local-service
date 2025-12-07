namespace BomLocalService.Models;

/// <summary>
/// Information about a cache folder containing cached data for a location.
/// Each cache folder represents one capture session and can contain multiple data types.
/// </summary>
public class CacheFolder
{
    /// <summary>
    /// The folder name (e.g., "Pomona_QLD_20251207_000906").
    /// </summary>
    public string FolderName { get; set; } = string.Empty;
    
    /// <summary>
    /// The UTC timestamp when this cache folder was created (extracted from folder name).
    /// </summary>
    public DateTime CacheTimestamp { get; set; }
    
    /// <summary>
    /// The observation time from the metadata (when the weather data was captured by BOM).
    /// </summary>
    public DateTime ObservationTime { get; set; }
    
    /// <summary>
    /// List of data types available in this cache folder.
    /// </summary>
    public List<CachedDataType> AvailableDataTypes { get; set; } = new();
    
    /// <summary>
    /// Whether this cache folder is complete and ready to use.
    /// </summary>
    public bool IsComplete { get; set; }
}
