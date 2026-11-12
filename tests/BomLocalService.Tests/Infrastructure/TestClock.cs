using System.Text.Json;
using BomLocalService.Services.Interfaces;

namespace BomLocalService.Tests.Infrastructure;

public sealed class TestClock : IClock
{
    private readonly object _lock = new();
    private DateTime _utcNow;

    public TestClock(DateTime utcNow)
    {
        _utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }
    }

    public void Advance(TimeSpan duration)
    {
        lock (_lock)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    public void Set(DateTime utcNow)
    {
        lock (_lock)
        {
            _utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        }
    }
}

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
