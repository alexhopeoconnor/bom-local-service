namespace BomLocalService.Models;

/// <summary>
/// Metadata for a single radar frame, stored alongside the frame image.
/// </summary>
public class FrameMetadata
{
    /// <summary>
    /// Frame index (0-6).
    /// </summary>
    public int FrameIndex { get; set; }
    
    /// <summary>
    /// The absolute UTC observation time for this frame.
    /// Parsed directly from the frame display label during capture.
    /// </summary>
    public DateTime ObservationTime { get; set; }
}

