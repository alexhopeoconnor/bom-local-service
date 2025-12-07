namespace BomLocalService.Models;

/// <summary>
/// Base model representing a single cached frame image.
/// Extended by type-specific frame models (RadarFrame, etc.).
/// </summary>
public class CachedFrame
{
    /// <summary>
    /// Frame index within the frameset (0-based).
    /// </summary>
    public int FrameIndex { get; set; }
    
    /// <summary>
    /// Full file system path to the frame image file (server-side only).
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;
    
    /// <summary>
    /// URL endpoint to retrieve this frame image.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// The data type this frame belongs to.
    /// </summary>
    public CachedDataType DataType { get; set; }
}
