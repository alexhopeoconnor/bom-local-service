namespace BomLocalService.Models;

/// <summary>
/// Represents a single radar frame (historical precipitation data).
/// Radar frames can be joined across cache folders to create extended historical slideshows.
/// </summary>
public class RadarFrame : CachedFrame
{
    /// <summary>
    /// The absolute UTC observation time for this frame.
    /// Parsed directly from the frame display label during capture.
    /// Client calculates "minutes ago" dynamically from this timestamp.
    /// </summary>
    public DateTime? AbsoluteObservationTime { get; set; }
    
    public RadarFrame()
    {
        DataType = CachedDataType.Radar;
    }
}

