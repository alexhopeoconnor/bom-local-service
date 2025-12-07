namespace BomLocalService.Models;

/// <summary>
/// Represents a single radar frame (historical precipitation data).
/// Frame 0 is oldest (40 minutes ago), Frame 6 is newest (10 minutes ago).
/// Radar frames can be joined across cache folders to create extended historical slideshows.
/// </summary>
public class RadarFrame : CachedFrame
{
    /// <summary>
    /// Number of minutes ago this frame represents (40, 35, 30, 25, 20, 15, 10).
    /// Frame 0 = 40 minutes ago, Frame 6 = 10 minutes ago.
    /// This is relative to the cache folder's observation time.
    /// </summary>
    public int MinutesAgo { get; set; }
    
    /// <summary>
    /// The absolute UTC observation time for this frame.
    /// Calculated as: ObservationTime - MinutesAgo.
    /// This is set when frames are joined across cache folders in timeseries responses.
    /// </summary>
    public DateTime? AbsoluteObservationTime { get; set; }
    
    public RadarFrame()
    {
        DataType = CachedDataType.Radar;
    }
}

