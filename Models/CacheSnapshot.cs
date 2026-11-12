using BomLocalService.Models;

namespace BomLocalService.Models;

/// <summary>
/// A validated, complete cache snapshot. Metadata and frames are always selected
/// from the same physical capture folder.
/// </summary>
public sealed class CacheSnapshot
{
    public string FolderName { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public DateTime CacheTimestampUtc { get; init; }
    public LastUpdatedInfo Metadata { get; init; } = new();
    public List<RadarFrame> Frames { get; init; } = new();
    public CachedDataType DataType { get; init; } = CachedDataType.Radar;

    public DateTime ObservationTime => Metadata.ObservationTime;
}
