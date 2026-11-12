using System.Collections.Concurrent;
using BomLocalService.Models;
using BomLocalService.Services;
using BomLocalService.Services.Interfaces;

namespace BomLocalService.Tests.Infrastructure;

public enum CaptureBehavior
{
    Succeed,
    Fail,
    BrowserFail,
    PauseThenSucceed,
    WaitForCancellation
}

public sealed class ControllableRadarCaptureService : IRadarCaptureService
{
    private readonly TestClock _clock;
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private int _active;
    private int _maxConcurrent;

    public ControllableRadarCaptureService(TestClock clock)
    {
        _clock = clock;
    }

    public CaptureBehavior Behavior { get; set; } = CaptureBehavior.Succeed;
    public int FrameCount { get; set; } = 7;
    public TimeSpan FrameSpacing { get; set; } = TimeSpan.FromMinutes(5);
    public DateTime? ObservationTime { get; set; }
    public TaskCompletionSource<bool> StartSignal { get; private set; } = NewSignal();
    public TaskCompletionSource<bool> ReleaseSignal { get; private set; } = NewSignal();
    public TaskCompletionSource<bool> CancellationObserved { get; private set; } = NewSignal();

    public int TotalCalls => _calls.Values.Sum();
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
    public int Active => Volatile.Read(ref _active);

    public int CallsFor(LocationId location) => _calls.GetValueOrDefault(location.Key);

    public void ResetSignals()
    {
        StartSignal = NewSignal();
        ReleaseSignal = NewSignal();
        CancellationObserved = NewSignal();
    }

    public async Task<RadarResponse> CaptureAsync(
        LocationId location,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        UpdateMaxConcurrent(active);
        _calls.AddOrUpdate(location.Key, 1, (_, count) => count + 1);
        StartSignal.TrySetResult(true);

        try
        {
            switch (Behavior)
            {
                case CaptureBehavior.Fail:
                    throw new RadarCaptureException("SCRAPING_ERROR", "simulated upstream failure");
                case CaptureBehavior.BrowserFail:
                    throw new RadarCaptureException("BROWSER_ERROR", "simulated Chromium disconnection");
                case CaptureBehavior.PauseThenSucceed:
                    await ReleaseSignal.Task.WaitAsync(cancellationToken);
                    break;
                case CaptureBehavior.WaitForCancellation:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    break;
            }

            var observation = ObservationTime ?? _clock.UtcNow.AddMinutes(-5);
            return TestCache.WriteStagingSnapshot(
                stagingDirectory,
                location,
                _clock.UtcNow,
                observation,
                FrameCount,
                FrameSpacing);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
            }

            Interlocked.Decrement(ref _active);
        }
    }

    private void UpdateMaxConcurrent(int active)
    {
        var current = Volatile.Read(ref _maxConcurrent);
        while (active > current)
        {
            var observed = Interlocked.CompareExchange(ref _maxConcurrent, active, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
