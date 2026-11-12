using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

public interface ILocationStateStore
{
    Task<LocationRuntimeState?> GetAsync(LocationId location, CancellationToken cancellationToken = default);
    Task SaveAsync(LocationRuntimeState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocationRuntimeState>> GetAllAsync(CancellationToken cancellationToken = default);
}
