using BomLocalService.Models;
using BomLocalService.Services.Interfaces;

namespace BomLocalService.Tests.Infrastructure;

/// <summary>
/// Deterministic state-store seam for lifecycle tests. It is only registered
/// when a test passes it explicitly to <see cref="BomServiceFactory"/>; the
/// default factory continues to use the real on-disk store.
/// </summary>
public sealed class ControllableLocationStateStore : ILocationStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LocationRuntimeState> _states = new(StringComparer.Ordinal);

    public bool BlockLoad { get; set; }
    public bool SaveResult { get; set; } = true;
    public Exception? SaveException { get; set; }

    public TaskCompletionSource<bool> LoadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ReleaseLoad { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int SaveCalls { get; private set; }

    public IReadOnlyList<LocationRuntimeState> States
    {
        get
        {
            lock (_gate)
            {
                return _states.Values.Select(CopyState).ToArray();
            }
        }
        set
        {
            lock (_gate)
            {
                _states.Clear();
                foreach (var state in value)
                {
                    if (LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
                    {
                        _states[location!.Key] = CopyState(state);
                    }
                }
            }
        }
    }

    public Task<LocationRuntimeState?> GetAsync(
        LocationId location,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(
                _states.TryGetValue(location.Key, out var state) ? CopyState(state) : null);
        }
    }

    public async Task<IReadOnlyList<LocationRuntimeState>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        LoadStarted.TrySetResult(true);

        if (BlockLoad)
        {
            await ReleaseLoad.Task.WaitAsync(cancellationToken);
        }

        lock (_gate)
        {
            return _states.Values.Select(CopyState).ToArray();
        }
    }

    public Task<bool> SaveAsync(
        LocationRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SaveException is not null)
        {
            throw SaveException;
        }

        lock (_gate)
        {
            SaveCalls++;
            if (!SaveResult)
            {
                return Task.FromResult(false);
            }

            if (LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
            {
                _states[location!.Key] = CopyState(state);
            }

            return Task.FromResult(true);
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
}
