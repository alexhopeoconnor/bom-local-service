using System.Net;
using System.Reflection;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Services;
using BomLocalService.Services.Interfaces;
using BomLocalService.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BomLocalService.Tests;

public class AdmissionLifecycleTests
{
    [Fact]
    public async Task Readiness_and_refresh_are_blocked_until_state_restore_completes()
    {
        var store = new ControllableLocationStateStore { BlockLoad = true };
        await using var app = new BomServiceFactory(stateStore: store);
        using var client = app.CreateClient();

        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var ready = await client.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }

        using (var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            var status = JsonSerializer.Deserialize<CacheUpdateStatus>(
                await refresh.Content.ReadAsStringAsync(), TestJson.Options)!;
            Assert.Equal("RejectedShuttingDown", status.RejectionCode);
        }

        Assert.Equal(0, app.Capture.TotalCalls);

        store.ReleaseLoad.TrySetResult(true);
        await WaitUntilAsync(
            async () => await IsReadyAsync(client),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Persisted_future_backoff_is_honoured_after_restore()
    {
        var store = new ControllableLocationStateStore { BlockLoad = true };
        store.States = new[]
        {
            new LocationRuntimeState
            {
                Suburb = "Pomona",
                State = "QLD",
                UpdateFailed = true,
                ErrorCode = "SCRAPING_ERROR",
                Error = "persisted failure",
                ConsecutiveFailures = 1,
                LastAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                NextRetryAt = DateTime.UtcNow.AddMinutes(10)
            }
        };

        await using var app = new BomServiceFactory(stateStore: store);
        using var client = app.CreateClient();

        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.ReleaseLoad.TrySetResult(true);
        await WaitUntilAsync(
            async () => await IsReadyAsync(client),
            TimeSpan.FromSeconds(10));

        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refresh.StatusCode);

        var status = JsonSerializer.Deserialize<CacheUpdateStatus>(
            await refresh.Content.ReadAsStringAsync(), TestJson.Options)!;
        Assert.Equal("RejectedBackoff", status.RejectionCode);
        Assert.Equal(0, app.Capture.TotalCalls);
    }

    [Fact]
    public async Task Save_failure_keeps_live_status_and_live_monitoring()
    {
        var store = new ControllableLocationStateStore
        {
            SaveResult = false,
            States = new[]
            {
                new LocationRuntimeState
                {
                    Suburb = "Pomona",
                    State = "QLD",
                    UpdateFailed = true,
                    ErrorCode = "SCRAPING_ERROR",
                    ConsecutiveFailures = 1,
                    LastAttemptAt = DateTime.UtcNow.AddMinutes(-1)
                }
            }
        };

        await using var app = new BomServiceFactory(stateStore: store);
        app.Capture.Behavior = CaptureBehavior.Succeed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-2);

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatusAsync(client);
            return status is { UpdateFailed: false, LastSuccessfulCaptureAt: not null };
        }, TimeSpan.FromSeconds(10));

        Assert.True(store.SaveCalls > 0);
        var live = await GetStatusAsync(client);
        Assert.False(live!.UpdateFailed);
        Assert.Null(live.ErrorCode);
        Assert.NotNull(live.LastSuccessfulCaptureAt);

        var queue = app.Services.GetRequiredService<ICacheRefreshQueue>();
        var expected = LocationId.Create("Pomona", "QLD");
        Assert.Contains(queue.GetMonitoredLocations(), location => location.Key == expected.Key);

        var snapshot = queue.GetRuntimeStateSnapshot(expected);
        Assert.NotNull(snapshot);
        snapshot!.UpdateFailed = true;
        Assert.False(queue.GetRuntimeStateSnapshot(expected)!.UpdateFailed);
    }

    [Fact]
    public async Task Failed_persistence_live_only_location_is_in_management_discovery()
    {
        var store = new ControllableLocationStateStore { SaveResult = false };
        await using var app = new BomServiceFactory(stateStore: store);
        app.Capture.Behavior = CaptureBehavior.Fail;

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatusAsync(client);
            return status is { UpdateFailed: true, CacheExists: false };
        }, TimeSpan.FromSeconds(10));

        var management = new CacheManagementService(
            app.Services.GetRequiredService<ILogger<CacheManagementService>>(),
            app.Services.GetRequiredService<IBomRadarService>(),
            app.Services.GetRequiredService<ICacheService>(),
            store,
            app.Services.GetRequiredService<ICacheRefreshQueue>(),
            app.Services.GetRequiredService<IBrowserService>(),
            app.Services.GetRequiredService<IConfiguration>());

        var method = typeof(CacheManagementService).GetMethod(
            "GetLocationsToManageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<List<LocationId>>)method.Invoke(
            management,
            new object[] { CancellationToken.None })!;
        var locations = await task;

        var expected = LocationId.Create("Pomona", "QLD");
        Assert.Contains(locations, location => location.Key == expected.Key);
    }

    [Fact]
    public async Task StopAsync_closes_admission_before_reader_loop_cancellation()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();

        var queue = app.Services.GetRequiredService<ICacheRefreshQueue>();
        var worker = app.Services.GetRequiredService<CacheRefreshService>();
        using var client = app.CreateClient();

        using var initial = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopTask = worker.StopAsync(CancellationToken.None);

        // StopAsync closes admission synchronously under _gate before its first await.
        var lateLocation = LocationId.Create("Sydney", "NSW");
        var accepted = queue.TryRequestRefresh(
            lateLocation,
            RefreshRequestSource.Api,
            out var rejected);

        Assert.False(accepted);
        Assert.Equal(RefreshRequestResultCode.RejectedShuttingDown, rejected.Code);
        Assert.Equal(0, queue.QueueDepth);

        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(queue.IsRunning);
        Assert.False(queue.GetStatus(lateLocation).IsQueuedOrRunning);
    }

    [Fact]
    public async Task Shutdown_stress_leaves_no_accepted_residual_or_queue_depth()
    {
        var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.WaitForCancellation;
        app.Capture.ResetSignals();

        var queue = app.Services.GetRequiredService<ICacheRefreshQueue>();
        var location = LocationId.Create("Pomona", "QLD");

        using var client = app.CreateClient();
        using var initial = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var requests = Enumerable.Range(0, 24)
            .Select(index => Task.Run(async () =>
            {
                try
                {
                    using var response = await client.PostAsync(
                        $"/api/cache/Shutdown{index}/QLD/refresh", null);
                }
                catch
                {
                    // Shutdown may close the in-flight request.
                }
            }))
            .ToArray();

        await app.DisposeAsync();
        await Task.WhenAll(requests);

        Assert.Equal(0, queue.QueueDepth);
        Assert.False(queue.GetStatus(location).IsQueuedOrRunning);
        Assert.False(queue.GetStatus(location).IsDeleting);
    }

    private static async Task<bool> IsReadyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/ready");
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static async Task<CacheUpdateStatus?> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/cache/Pomona/QLD/status");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonSerializer.Deserialize<CacheUpdateStatus>(
            await response.Content.ReadAsStringAsync(), TestJson.Options);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Condition was not met within {timeout}.");
    }
}
