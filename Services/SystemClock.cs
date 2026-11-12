using BomLocalService.Services.Interfaces;

namespace BomLocalService.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
