using System.Collections.Concurrent;
using System.Threading.Channels;
using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

/// <summary>
/// Single owner of background refresh work: bounded queue, per-location
/// deduplication, capture deadline, retry backoff and orderly shutdown.
/// </summary>
public sealed class CacheRefreshService : BackgroundService, ICacheRefreshQueue
{
    private enum WorkerPhase
    {
        Created,
        Restoring,
        Accepting,
        Stopping,
        Stopped
    }

    private sealed class Reservation
    {
        public required LocationId Location { get; init; }
        public DateTime RequestedAtUtc { get; init; }
        public DateTime? StartedAtUtc { get; set; }
    }

    private readonly ILogger<CacheRefreshService> _logger;
    private readonly ICacheService _cacheService;
    private readonly IRadarCaptureService _captureService;
    private readonly ILocationStateStore _stateStore;
    private readonly IClock _clock;
    private readonly Channel<RefreshRequest> _queue;
    private readonly int _queueCapacity;
    private readonly int _maxConcurrentCaptures;
    private readonly TimeSpan _captureTimeout;
    private readonly TimeSpan _backoffBase;
    private readonly TimeSpan _maxBackoff;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Reservation> _reservations = new();
    private readonly ConcurrentDictionary<string, byte> _deletions = new();
    private readonly ConcurrentDictionary<string, LocationRuntimeState> _states = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private int _phase = (int)WorkerPhase.Created;
    private int _queueDepth;
    private int _activeCaptures;

    private sealed record RefreshRequest(LocationId Location, RefreshRequestSource Source, DateTime RequestedAtUtc);

    public CacheRefreshService(
        ILogger<CacheRefreshService> logger,
        IConfiguration configuration,
        ICacheService cacheService,
        IRadarCaptureService captureService,
        ILocationStateStore stateStore,
        IClock clock)
    {
        _logger = logger;
        _cacheService = cacheService;
        _captureService = captureService;
        _stateStore = stateStore;
        _clock = clock;

        _queueCapacity = configuration.GetValue<int?>("Refresh:QueueCapacity") ?? 16;
        if (_queueCapacity is < 1 or > 1000)
        {
            throw new ArgumentException("Refresh:QueueCapacity must be between 1 and 1000", nameof(configuration));
        }

        _maxConcurrentCaptures = configuration.GetValue<int?>("Refresh:MaxConcurrentCaptures") ?? 1;
        if (_maxConcurrentCaptures is < 1 or > 16)
        {
            throw new ArgumentException("Refresh:MaxConcurrentCaptures must be between 1 and 16", nameof(configuration));
        }

        var timeoutSeconds = configuration.GetValue<int?>("Refresh:CaptureTimeoutSeconds") ?? 300;
        if (timeoutSeconds < 1 || timeoutSeconds > 3600)
        {
            throw new ArgumentException("Refresh:CaptureTimeoutSeconds must be between 1 and 3600", nameof(configuration));
        }

        _captureTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        _backoffBase = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>("Refresh:BackoffBaseSeconds") ?? 60));
        _maxBackoff = TimeSpan.FromSeconds(Math.Max(_backoffBase.TotalSeconds, configuration.GetValue<int?>("Refresh:MaxBackoffSeconds") ?? 900));
        _concurrency = new SemaphoreSlim(_maxConcurrentCaptures, _maxConcurrentCaptures);

        _queue = Channel.CreateBounded<RefreshRequest>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    private WorkerPhase Phase => (WorkerPhase)Volatile.Read(ref _phase);

    public bool IsRunning => Phase == WorkerPhase.Accepting;

    public int QueueDepth
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, _queueDepth);
            }
        }
    }

    public RefreshQueueStatus GetStatus(LocationId location)
    {
        lock (_gate)
        {
            var active = _reservations.ContainsKey(location.Key);
            var deleting = _deletions.ContainsKey(location.Key);
            return new RefreshQueueStatus(
                active,
                Phase == WorkerPhase.Accepting,
                deleting,
                Math.Max(0, _queueDepth),
                Volatile.Read(ref _activeCaptures));
        }
    }

    public bool IsLocationActive(LocationId location)
    {
        lock (_gate)
        {
            return _reservations.ContainsKey(location.Key) || _deletions.ContainsKey(location.Key);
        }
    }

    public LocationRuntimeState? GetRuntimeStateSnapshot(LocationId location)
    {
        if (!_states.TryGetValue(location.Key, out var state))
        {
            return null;
        }

        lock (state)
        {
            return CopyState(state);
        }
    }

    public IReadOnlyList<LocationId> GetMonitoredLocations()
    {
        var locations = new List<LocationId>();

        foreach (var state in _states.Values)
        {
            if (LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
            {
                locations.Add(location!);
            }
        }

        return Array.AsReadOnly(locations
            .OrderBy(location => location.Key, StringComparer.Ordinal)
            .ToArray());
    }

    public bool TryRequestRefresh(
        LocationId location,
        RefreshRequestSource source,
        out RefreshRequestResult result)
    {
        var accepted = false;
        var queueFull = false;

        lock (_gate)
        {
            if (Phase != WorkerPhase.Accepting)
            {
                result = new RefreshRequestResult(
                    RefreshRequestResultCode.RejectedShuttingDown,
                    false,
                    "Refresh worker is not accepting work",
                    null,
                    Math.Max(0, _queueDepth));
                return false;
            }

            if (_deletions.ContainsKey(location.Key))
            {
                result = new RefreshRequestResult(
                    RefreshRequestResultCode.RejectedConflict,
                    false,
                    "Cache deletion is in progress for this location",
                    null,
                    Math.Max(0, _queueDepth));
                return false;
            }

            if (_reservations.TryGetValue(location.Key, out var existing))
            {
                result = new RefreshRequestResult(
                    RefreshRequestResultCode.AlreadyInProgress,
                    true,
                    existing.StartedAtUtc.HasValue
                        ? "Cache update is currently running"
                        : "Cache update is already queued",
                    null,
                    Math.Max(0, _queueDepth));
                return true;
            }

            if (_states.TryGetValue(location.Key, out var state))
            {
                // _gate -> state is the only nesting order; state-only readers
                // and UpdateStateAsync never acquire _gate while holding state.
                DateTime? nextRetryAt;
                lock (state)
                {
                    nextRetryAt = state.NextRetryAt;
                }

                if (nextRetryAt.HasValue && nextRetryAt.Value > _clock.UtcNow)
                {
                    result = new RefreshRequestResult(
                        RefreshRequestResultCode.RejectedBackoff,
                        false,
                        "Previous update failed; retry backoff is still active",
                        nextRetryAt,
                        Math.Max(0, _queueDepth));
                    return false;
                }
            }

            var reservation = new Reservation
            {
                Location = location,
                RequestedAtUtc = _clock.UtcNow
            };
            _reservations[location.Key] = reservation;

            var request = new RefreshRequest(location, source, _clock.UtcNow);
            _queueDepth++;

            if (_queue.Writer.TryWrite(request))
            {
                accepted = true;
                result = new RefreshRequestResult(
                    RefreshRequestResultCode.Accepted,
                    true,
                    "Cache update accepted",
                    null,
                    _queueDepth);
            }
            else
            {
                _queueDepth--;
                _reservations.TryRemove(location.Key, out _);
                queueFull = true;
                result = new RefreshRequestResult(
                    RefreshRequestResultCode.RejectedQueueFull,
                    false,
                    "Refresh queue is full",
                    null,
                    Math.Max(0, _queueDepth));
            }
        }

        if (accepted)
        {
            _logger.LogInformation("Queued refresh for {Location} from {Source} (queue depth {Depth})",
                location, source, result.QueueDepth);
        }
        else if (queueFull)
        {
            _logger.LogWarning("Rejected refresh for {Location}: queue is full", location);
        }

        return accepted;
    }

    public bool TryBeginDelete(LocationId location, out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (_reservations.ContainsKey(location.Key))
            {
                error = "A capture is queued or running for this location";
                return false;
            }

            if (!_deletions.TryAdd(location.Key, 0))
            {
                error = "A cache deletion is already in progress for this location";
                return false;
            }

            return true;
        }
    }

    public void EndDelete(LocationId location)
    {
        lock (_gate)
        {
            _deletions.TryRemove(location.Key, out _);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Phase is WorkerPhase.Created or WorkerPhase.Restoring or WorkerPhase.Accepting)
            {
                Volatile.Write(ref _phase, (int)WorkerPhase.Stopping);
            }

            _queue.Writer.TryComplete();
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (Phase == WorkerPhase.Created)
            {
                Volatile.Write(ref _phase, (int)WorkerPhase.Restoring);
            }
        }

        if (Phase != WorkerPhase.Restoring)
        {
            lock (_gate)
            {
                Volatile.Write(ref _phase, (int)WorkerPhase.Stopped);
            }

            return;
        }

        _logger.LogInformation(
            "Refresh worker restoring persisted state (capacity {Capacity}, concurrency {Concurrency}, timeout {Timeout}s)",
            _queueCapacity, _maxConcurrentCaptures, _captureTimeout.TotalSeconds);

        try
        {
            try
            {
                await LoadPersistedStatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh worker could not restore persisted state; admission remains closed");
                return;
            }

            lock (_gate)
            {
                if (Phase != WorkerPhase.Restoring || stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                Volatile.Write(ref _phase, (int)WorkerPhase.Accepting);
            }

            _logger.LogInformation("Refresh worker accepting work");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _concurrency.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                RefreshRequest request;
                try
                {
                    request = await _queue.Reader.ReadAsync(stoppingToken);
                    lock (_gate)
                    {
                        _queueDepth--;
                    }
                }
                catch (OperationCanceledException)
                {
                    _concurrency.Release();
                    break;
                }
                catch (ChannelClosedException)
                {
                    _concurrency.Release();
                    break;
                }

                var task = ProcessRequestAsync(request, stoppingToken);
                _inFlight[task] = 0;
                _ = task.ContinueWith(completed =>
                {
                    _inFlight.TryRemove(completed, out _);
                    _concurrency.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (Phase != WorkerPhase.Stopped)
                {
                    Volatile.Write(ref _phase, (int)WorkerPhase.Stopping);
                }

                _queue.Writer.TryComplete();
            }

            while (_queue.Reader.TryRead(out var unprocessed))
            {
                lock (_gate)
                {
                    _queueDepth--;
                }

                ReleaseReservation(unprocessed.Location);
            }

            var pending = _inFlight.Keys.ToArray();
            if (pending.Length > 0)
            {
                _logger.LogInformation("Waiting for {Count} in-flight capture(s) to stop", pending.Length);
                try
                {
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "In-flight captures did not stop cleanly");
                }
            }

            lock (_gate)
            {
                Volatile.Write(ref _phase, (int)WorkerPhase.Stopped);
            }
        }

        _logger.LogInformation("Refresh worker stopped");
    }

    private async Task LoadPersistedStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var state in await _stateStore.GetAllAsync(cancellationToken))
        {
            if (LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
            {
                _states[location!.Key] = state;
            }
        }
    }

    private async Task ProcessRequestAsync(RefreshRequest request, CancellationToken stoppingToken)
    {
        var location = request.Location;
        Interlocked.Increment(ref _activeCaptures);
        var startedAt = _clock.UtcNow;
        string? stagingDirectory = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(_captureTimeout);

        lock (_gate)
        {
            if (_reservations.TryGetValue(location.Key, out var reservation))
            {
                reservation.StartedAtUtc = startedAt;
            }
        }

        try
        {
            _logger.LogInformation("Starting capture for {Location} (requested by {Source})", location, request.Source);
            await UpdateStateAsync(location, state =>
            {
                state.LastAttemptAt = startedAt;
                state.UpdatedAt = startedAt;
            }, CancellationToken.None);

            stagingDirectory = _cacheService.CreateStagingDirectory(location, startedAt);
            var capture = await _captureService.CaptureAsync(location, stagingDirectory, deadline.Token);
            var snapshot = await _cacheService.PublishSnapshotAsync(location, stagingDirectory, CachedDataType.Radar, deadline.Token);
            stagingDirectory = null;

            await UpdateStateAsync(location, state =>
            {
                state.LastSuccessfulCaptureAt = _clock.UtcNow;
                state.LatestObservationAt = snapshot.ObservationTime;
                state.UpdateFailed = false;
                state.ErrorCode = null;
                state.Error = null;
                state.ConsecutiveFailures = 0;
                state.NextRetryAt = null;
            }, CancellationToken.None);

            _logger.LogInformation("Capture succeeded for {Location}: {Folder} observed {ObservationTime}",
                location, snapshot.FolderName, snapshot.ObservationTime);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Capture for {Location} cancelled by application shutdown", location);
        }
        catch (Exception ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Capture for {Location} stopped during application shutdown", location);
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(location, "TIMEOUT_ERROR", $"Capture exceeded the {_captureTimeout.TotalSeconds:F0}s deadline");
        }
        catch (RadarCaptureException ex)
        {
            await RecordFailureAsync(location, ex.ErrorCode, ex.Message);
        }
        catch (InvalidSnapshotException ex)
        {
            await RecordFailureAsync(location, ex.ErrorCode, ex.Message);
        }
        catch (InvalidCacheFolderException ex)
        {
            await RecordFailureAsync(location, "STORAGE_ERROR", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RecordFailureAsync(location, "STORAGE_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(location, "SCRAPING_ERROR", ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDirectory))
            {
                await DeleteAbandonedStagingAsync(stagingDirectory);
            }

            ReleaseReservation(location);
            Interlocked.Decrement(ref _activeCaptures);
        }
    }

    private async Task RecordFailureAsync(LocationId location, string errorCode, string message)
    {
        var bounded = BoundError(message);
        LocationRuntimeState? updated = null;

        await UpdateStateAsync(location, state =>
        {
            state.ConsecutiveFailures = Math.Max(1, state.ConsecutiveFailures + 1);
            state.UpdateFailed = true;
            state.ErrorCode = errorCode;
            state.Error = bounded;
            var backoff = ComputeBackoff(state.ConsecutiveFailures);
            state.NextRetryAt = _clock.UtcNow.Add(backoff);
            updated = state;
        }, CancellationToken.None);

        _logger.LogWarning(
            "Capture failed for {Location}: {ErrorCode}: {Error}. Next retry after {NextRetry}",
            location, errorCode, bounded, updated?.NextRetryAt);
    }

    private TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 10));
        var seconds = Math.Min(_maxBackoff.TotalSeconds, _backoffBase.TotalSeconds * factor);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<bool> UpdateStateAsync(
        LocationId location,
        Action<LocationRuntimeState> update,
        CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(location.Key, _ => new LocationRuntimeState
        {
            Suburb = location.Suburb,
            State = location.State,
            UpdatedAt = _clock.UtcNow
        });

        lock (state)
        {
            state.Suburb = location.Suburb;
            state.State = location.State;
            update(state);
            state.UpdatedAt = _clock.UtcNow;
        }

        bool persisted;
        try
        {
            persisted = await _stateStore.SaveAsync(state, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist runtime state for {Location}", location);
            return false;
        }

        if (!persisted)
        {
            _logger.LogWarning(
                "Runtime state for {Location} is live but was not persisted; durable status may be stale",
                location);
        }

        return persisted;
    }

    private void ReleaseReservation(LocationId location)
    {
        lock (_gate)
        {
            _reservations.TryRemove(location.Key, out _);
        }
    }

    private static LocationRuntimeState CopyState(LocationRuntimeState state) => new()
    {
        Suburb = state.Suburb,
        State = state.State,
        LastAttemptAt = state.LastAttemptAt,
        LastSuccessfulCaptureAt = state.LastSuccessfulCaptureAt,
        LatestObservationAt = state.LatestObservationAt,
        NextRetryAt = state.NextRetryAt,
        UpdateFailed = state.UpdateFailed,
        ErrorCode = state.ErrorCode,
        Error = state.Error,
        ConsecutiveFailures = state.ConsecutiveFailures,
        UpdatedAt = state.UpdatedAt
    };

    private Task DeleteAbandonedStagingAsync(string stagingDirectory)
    {
        var folderName = Path.GetFileName(stagingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var relativePath = $"{FilePathHelper.StagingDirectoryName}/{folderName}";
        var cacheRoot = _cacheService.GetCacheDirectory();

        try
        {
            if (FilePathHelper.TryDeleteOwnedTree(cacheRoot, relativePath, out var error))
            {
                _logger.LogDebug("Removed abandoned staging directory: {Folder}", folderName);
            }
            else
            {
                _logger.LogWarning(
                    "Refusing to remove abandoned staging directory {Folder}: {Error}",
                    relativePath,
                    error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to remove abandoned staging directory {Folder}; nothing else was deleted",
                relativePath);
        }

        return Task.CompletedTask;
    }

    private static string BoundError(string message)
    {
        var firstLine = message.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 300 ? firstLine : firstLine[..300];
    }
}
