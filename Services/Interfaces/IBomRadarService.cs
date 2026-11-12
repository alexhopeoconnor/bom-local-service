using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Interfaces;

public interface IBomRadarService : ISingletonService
{
    Task<RadarResponse?> GetCachedRadarAsync(LocationId location, CancellationToken cancellationToken = default);
    Task<CacheUpdateStatus> TriggerCacheUpdateAsync(LocationId location, RefreshRequestSource source = RefreshRequestSource.Api, CancellationToken cancellationToken = default);
    Task<CacheUpdateStatus> GetCacheStatusAsync(LocationId location, CancellationToken cancellationToken = default);
    Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(LocationId location, CancellationToken cancellationToken = default);
    Task<List<RadarFrame>?> GetCachedFramesAsync(LocationId location, CancellationToken cancellationToken = default);
    Task<RadarFrame?> GetCachedFrameAsync(LocationId location, int frameIndex, CancellationToken cancellationToken = default);
    Task<CacheRange> GetCacheRangeAsync(LocationId location, CancellationToken cancellationToken = default);
    Task<RadarTimeSeriesResponse> GetRadarTimeSeriesAsync(
        LocationId location,
        DateTime? startTime,
        DateTime? endTime,
        CancellationToken cancellationToken = default);
    Task<RadarFrame?> GetFrameFromCacheFolderAsync(
        LocationId location,
        string cacheFolderName,
        int frameIndex,
        CancellationToken cancellationToken = default);
}
