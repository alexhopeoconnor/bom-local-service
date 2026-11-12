using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Performs one browser capture into a staging directory. Implementations must
/// not publish the snapshot or touch the live cache namespace.
/// </summary>
public interface IRadarCaptureService
{
    Task<RadarResponse> CaptureAsync(
        LocationId location,
        string stagingDirectory,
        CancellationToken cancellationToken);
}
