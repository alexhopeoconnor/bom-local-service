namespace BomLocalService.Models;

/// <summary>
/// Types of cached data available from BOM weather pages.
/// </summary>
public enum CachedDataType
{
    /// <summary>
    /// Rain radar images showing historical precipitation (40 min ago → 10 min ago).
    /// Frames represent a time series and can be joined across cache folders for extended historical slideshows.
    /// </summary>
    Radar
    // Future data types (not yet implemented):
    // Temperature - forecast images showing current conditions and projected temperatures for the week ahead
    // Wind - forecast images showing current conditions and projected wind patterns for the week ahead
}
