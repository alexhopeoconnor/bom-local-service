using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

public sealed class BomRadarService : IBomRadarService
{
    private readonly ILogger<BomRadarService> _logger;
    private readonly ICacheService _cacheService;
    private readonly ICacheRefreshQueue _refreshQueue;
    private readonly ILocationStateStore _stateStore;
    private readonly IClock _clock;
    private readonly double _cacheExpirationMinutes;
    private readonly int _cacheManagementCheckIntervalMinutes;
    private readonly int _estimatedUpdateDurationSeconds;
    private readonly int _timeSeriesWarningFolderCount;

    public BomRadarService(
        ILogger<BomRadarService> logger,
        ICacheService cacheService,
        ICacheRefreshQueue refreshQueue,
        ILocationStateStore stateStore,
        IClock clock,
        IConfiguration configuration)
    {
        _logger = logger;
        _cacheService = cacheService;
        _refreshQueue = refreshQueue;
        _stateStore = stateStore;
        _clock = clock;

        _cacheExpirationMinutes = configuration.GetValue<double?>("CacheExpirationMinutes")
            ?? throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");

        _cacheManagementCheckIntervalMinutes = configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes")
            ?? throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");

        _timeSeriesWarningFolderCount = configuration.GetValue<int?>("TimeSeries:WarningFolderCount")
            ?? throw new InvalidOperationException("TimeSeries:WarningFolderCount configuration is required. Set it in appsettings.json or via TIMESERIES__WARNINGFOLDERCOUNT environment variable.");

        _estimatedUpdateDurationSeconds = CacheHelper.GetEstimatedUpdateDurationSeconds(configuration, CachedDataType.Radar);

        if (_cacheExpirationMinutes <= 0)
        {
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0", nameof(configuration));
        }

        if (_cacheManagementCheckIntervalMinutes is <= 0 or > 60)
        {
            throw new ArgumentException("CacheManagement:CheckIntervalMinutes must be between 1 and 60", nameof(configuration));
        }

        if (_timeSeriesWarningFolderCount <= 0)
        {
            throw new ArgumentException("TimeSeries:WarningFolderCount must be greater than 0", nameof(configuration));
        }
    }

    public async Task<RadarResponse?> GetCachedRadarAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetLatestSnapshotAsync(location, CachedDataType.Radar, cancellationToken);
        if (snapshot == null)
        {
            return null;
        }

        var queueStatus = _refreshQueue.GetStatus(location);
        var isValid = _cacheService.IsCacheValid(snapshot.Metadata);
        var cacheExpiresAt = snapshot.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        var estimatedRemaining = _refreshQueue.GetStatus(location).IsQueuedOrRunning
            ? _cacheService.GetEstimatedRemainingSeconds(location.Key)
            : 0;
        var durationToUse = estimatedRemaining > 0 ? estimatedRemaining : _estimatedUpdateDurationSeconds;

        return ResponseBuilder.CreateRadarResponse(
            snapshot.FolderPath,
            snapshot.Frames,
            _cacheManagementCheckIntervalMinutes,
            snapshot.Metadata,
            location.Suburb,
            location.State,
            isValid,
            cacheExpiresAt,
            queueStatus.IsQueuedOrRunning,
            durationToUse,
            snapshot.FolderName);
    }

    public async Task<CacheUpdateStatus> TriggerCacheUpdateAsync(
        LocationId location,
        RefreshRequestSource source = RefreshRequestSource.Api,
        CancellationToken cancellationToken = default)
    {
        var status = await GetCacheStatusAsync(location, cancellationToken);
        if (status.CacheIsValid)
        {
            status.Message = "Cache is valid, no update needed";
            status.NextUpdateTime = status.CacheExpiresAt;
            return status;
        }

        var accepted = _refreshQueue.TryRequestRefresh(location, source, out var result);
        status.QueueDepth = result.QueueDepth;
        status.IsUpdating = _refreshQueue.GetStatus(location).IsQueuedOrRunning;
        status.UpdateAccepted = result.Code == RefreshRequestResultCode.Accepted;
        status.UpdateTriggered = result.Code == RefreshRequestResultCode.Accepted;

        if (accepted)
        {
            status.Message = result.Code == RefreshRequestResultCode.AlreadyInProgress
                ? "Cache update already in progress"
                : status.CacheExists
                    ? "Cache is stale, update triggered"
                    : "No cache exists, update triggered";
            status.NextUpdateTime = _clock.UtcNow.AddSeconds(_estimatedUpdateDurationSeconds);
        }
        else
        {
            status.RejectionCode = result.Code.ToString();
            status.Message = result.Message ?? "Cache update was not accepted";
            status.NextRetryAt = result.NextRetryAt;
            status.RetryAfterSeconds = result.NextRetryAt.HasValue
                ? Math.Max(1, (int)Math.Ceiling((result.NextRetryAt.Value - _clock.UtcNow).TotalSeconds))
                : null;
        }

        return status;
    }

    public async Task<CacheUpdateStatus> GetCacheStatusAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetLatestSnapshotAsync(location, CachedDataType.Radar, cancellationToken);
        var state = _refreshQueue.GetRuntimeStateSnapshot(location)
            ?? await _stateStore.GetAsync(location, cancellationToken);
        var queueStatus = _refreshQueue.GetStatus(location);

        var status = new CacheUpdateStatus
        {
            CacheExists = snapshot != null,
            CacheIsValid = snapshot != null && _cacheService.IsCacheValid(snapshot.Metadata),
            IsUpdating = queueStatus.IsQueuedOrRunning,
            QueueDepth = queueStatus.QueueDepth,
            LatestObservationAt = snapshot?.ObservationTime,
            LastSuccessfulCaptureAt = state?.LastSuccessfulCaptureAt ?? snapshot?.CacheTimestampUtc,
            LastUpdateAttempt = state?.LastAttemptAt,
            UpdateFailed = state?.UpdateFailed ?? false,
            ErrorCode = state?.ErrorCode,
            Error = state?.Error,
            ConsecutiveFailures = state?.ConsecutiveFailures ?? 0,
            NextRetryAt = state?.NextRetryAt
        };

        if (snapshot != null)
        {
            status.CacheExpiresAt = snapshot.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        }

        if (queueStatus.IsQueuedOrRunning)
        {
            status.Message = queueStatus.IsDeleting
                ? "Cache deletion is in progress"
                : "Cache update is in progress";
            var remaining = _cacheService.GetEstimatedRemainingSeconds(location.Key);
            status.NextUpdateTime = _clock.UtcNow.AddSeconds(remaining > 0 ? remaining : _estimatedUpdateDurationSeconds);
        }
        else if (status.UpdateFailed)
        {
            status.Message = "Last cache update failed";
            status.NextUpdateTime = status.NextRetryAt;
        }
        else if (status.CacheIsValid)
        {
            status.Message = "Cache is valid, no update needed";
            status.NextUpdateTime = status.CacheExpiresAt;
        }
        else if (status.CacheExists)
        {
            status.Message = "Cache is stale";
            status.NextUpdateTime = status.NextRetryAt ?? ResponseBuilder.CalculateNextServiceCheck(_cacheManagementCheckIntervalMinutes);
        }
        else
        {
            status.Message = "No cache exists";
            status.NextUpdateTime = status.NextRetryAt ?? ResponseBuilder.CalculateNextServiceCheck(_cacheManagementCheckIntervalMinutes);
        }

        if (status.NextRetryAt.HasValue && status.NextRetryAt.Value > _clock.UtcNow)
        {
            status.RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling((status.NextRetryAt.Value - _clock.UtcNow).TotalSeconds));
        }

        return status;
    }

    public async Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetLatestSnapshotAsync(location, CachedDataType.Radar, cancellationToken);
        return snapshot?.Metadata;
    }

    public async Task<List<RadarFrame>?> GetCachedFramesAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetLatestSnapshotAsync(location, CachedDataType.Radar, cancellationToken);
        if (snapshot == null || snapshot.Frames.Count == 0)
        {
            return null;
        }

        foreach (var frame in snapshot.Frames)
        {
            frame.ImageUrl = BuildFrameUrl(location, frame.FrameIndex, snapshot.FolderName);
        }

        return snapshot.Frames;
    }

    public async Task<RadarFrame?> GetCachedFrameAsync(LocationId location, int frameIndex, CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetLatestSnapshotAsync(location, CachedDataType.Radar, cancellationToken);
        var frame = snapshot?.Frames.FirstOrDefault(f => f.FrameIndex == frameIndex);
        if (frame != null)
        {
            frame.ImageUrl = BuildFrameUrl(location, frameIndex, snapshot!.FolderName);
        }

        return frame;
    }

    public async Task<CacheRange> GetCacheRangeAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var snapshots = await _cacheService.GetSnapshotsAsync(location, CachedDataType.Radar, cancellationToken);
        if (snapshots.Count == 0)
        {
            return new CacheRange { TotalCacheFolders = 0 };
        }

        var oldest = snapshots[0];
        var newest = snapshots[^1];
        var observations = snapshots
            .SelectMany(s => s.Frames)
            .Where(f => f.AbsoluteObservationTime.HasValue)
            .Select(f => f.AbsoluteObservationTime!.Value)
            .ToArray();

        return new CacheRange
        {
            OldestCache = ToCacheFolder(oldest),
            NewestCache = ToCacheFolder(newest),
            TotalCacheFolders = snapshots.Count,
            TimeSpanMinutes = snapshots.Count >= 2
                ? (int?)(newest.CacheTimestampUtc - oldest.CacheTimestampUtc).TotalMinutes
                : null,
            OldestObservationTime = observations.Length > 0 ? observations.Min() : null,
            NewestObservationTime = observations.Length > 0 ? observations.Max() : null
        };
    }

    public async Task<RadarTimeSeriesResponse> GetRadarTimeSeriesAsync(
        LocationId location,
        DateTime? startTime,
        DateTime? endTime,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await _cacheService.GetSnapshotsAsync(location, CachedDataType.Radar, cancellationToken);
        var startUtc = NormalizeUtc(startTime);
        var endUtc = NormalizeUtc(endTime);

        if (snapshots.Count > _timeSeriesWarningFolderCount)
        {
            _logger.LogWarning(
                "Processing large number of cache folders ({Count}) for time series request. Location: {Location}, StartTime: {StartTime}, EndTime: {EndTime}",
                snapshots.Count, location, startUtc, endUtc);
        }

        var entries = snapshots
            .SelectMany(snapshot => snapshot.Frames
                .Where(frame => frame.AbsoluteObservationTime.HasValue)
                .Select(frame => new TimeSeriesEntry(
                    frame.AbsoluteObservationTime!.Value,
                    snapshot.CacheTimestampUtc,
                    snapshot.FolderName,
                    snapshot,
                    frame)))
            .Where(entry => (!startUtc.HasValue || entry.ObservedAt >= startUtc.Value) &&
                            (!endUtc.HasValue || entry.ObservedAt <= endUtc.Value))
            .GroupBy(entry => entry.ObservedAt)
            .Select(group => group
                .OrderByDescending(entry => entry.CapturedAt)
                .ThenBy(entry => entry.FolderName, StringComparer.Ordinal)
                .First())
            .OrderBy(entry => entry.ObservedAt)
            .ToArray();

        var folders = entries
            .GroupBy(entry => entry.FolderName)
            .Select(group =>
            {
                var first = group.First();
                var frames = group
                    .OrderBy(entry => entry.ObservedAt)
                    .Select(entry =>
                    {
                        entry.Frame.ImageUrl = BuildFrameUrl(location, entry.Frame.FrameIndex, entry.FolderName);
                        return entry.Frame;
                    })
                    .ToList();

                return new RadarCacheFolderFrames
                {
                    CacheFolderName = first.FolderName,
                    CacheTimestamp = first.CapturedAt,
                    ObservationTime = first.Snapshot.ObservationTime,
                    Frames = frames
                };
            })
            .OrderBy(folder => folder.Frames.Min(frame => frame.AbsoluteObservationTime ?? DateTime.MaxValue))
            .ThenBy(folder => folder.CacheFolderName, StringComparer.Ordinal)
            .ToList();

        return new RadarTimeSeriesResponse
        {
            CacheFolders = folders,
            StartTime = startUtc,
            EndTime = endUtc,
            TotalFrames = entries.Length
        };
    }

    public async Task<RadarFrame?> GetFrameFromCacheFolderAsync(
        LocationId location,
        string cacheFolderName,
        int frameIndex,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _cacheService.GetSnapshotContainingFrameAsync(
            location,
            cacheFolderName,
            frameIndex,
            CachedDataType.Radar,
            cancellationToken);

        var frame = snapshot?.Frames.FirstOrDefault(f => f.FrameIndex == frameIndex);
        if (frame != null)
        {
            frame.ImageUrl = BuildFrameUrl(location, frameIndex, snapshot!.FolderName);
        }

        return frame;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static CacheFolder ToCacheFolder(CacheSnapshot snapshot) => new()
    {
        FolderName = snapshot.FolderName,
        CacheTimestamp = snapshot.CacheTimestampUtc,
        ObservationTime = snapshot.ObservationTime,
        AvailableDataTypes = new List<CachedDataType> { snapshot.DataType },
        IsComplete = true
    };

    private static string BuildFrameUrl(LocationId location, int frameIndex, string cacheFolderName) =>
        $"/api/radar/{Uri.EscapeDataString(location.Suburb)}/{Uri.EscapeDataString(location.State)}/frame/{frameIndex}" +
        $"?cacheFolder={Uri.EscapeDataString(cacheFolderName)}";

    private sealed record TimeSeriesEntry(
        DateTime ObservedAt,
        DateTime CapturedAt,
        string FolderName,
        CacheSnapshot Snapshot,
        RadarFrame Frame);
}
