namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Injectable clock for expiry, retention, retry and capture timestamps.
/// Tests use a controllable implementation.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
