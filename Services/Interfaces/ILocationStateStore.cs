using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

public interface ILocationStateStore
{
    Task<LocationRuntimeState?> GetAsync(LocationId location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a defensive copy atomically. Returns true only after the temp
    /// file has been flushed and renamed over the target. Recoverable IO/schema
    /// failures return false and are logged; caller cancellation propagates.
    /// The store does not mutate <see cref="LocationRuntimeState.UpdatedAt"/>.
    /// </summary>
    Task<bool> SaveAsync(LocationRuntimeState state, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocationRuntimeState>> GetAllAsync(CancellationToken cancellationToken = default);
}
