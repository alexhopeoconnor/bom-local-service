using System.Net;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Tests.Infrastructure;
using Xunit;

namespace BomLocalService.Tests;

public class OperationalStatusTests
{
    [Fact]
    public async Task Empty_cache_liveness_and_readiness_do_not_scrape()
    {
        await using var app = new BomServiceFactory();
        using var client = app.CreateClient();

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var readyJson = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("ready", readyJson.RootElement.GetProperty("status").GetString());
        Assert.True(readyJson.RootElement.GetProperty("checks").GetProperty("cacheWritable").GetBoolean());
        Assert.True(readyJson.RootElement.GetProperty("checks").GetProperty("refreshWorker").GetBoolean());

        Assert.Equal(0, app.Capture.TotalCalls);

        using var statusResponse = await client.GetAsync("/api/cache/Pomona/QLD/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = JsonSerializer.Deserialize<CacheUpdateStatus>(await statusResponse.Content.ReadAsStringAsync(), TestJson.Options)!;
        Assert.False(status.CacheExists);
        Assert.False(status.IsUpdating);
        Assert.False(status.UpdateFailed);
        Assert.Null(status.LatestObservationAt);
        Assert.Null(status.LastSuccessfulCaptureAt);
    }

    [Fact]
    public async Task Cold_start_reports_progress_then_fresh_status_after_capture()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-5);
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var cold = await client.GetAsync("/api/radar/Pomona/QLD");
        Assert.Equal(HttpStatusCode.NotFound, cold.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = await GetStatus(client);
        Assert.True(running.IsUpdating);
        Assert.False(running.CacheExists);

        using var liveHealth = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, liveHealth.StatusCode);
        using var liveReady = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, liveReady.StatusCode);

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () => (await client.GetAsync("/api/radar/Pomona/QLD")).StatusCode == HttpStatusCode.OK);

        var fresh = await GetStatus(client);
        Assert.True(fresh.CacheExists);
        Assert.True(fresh.CacheIsValid);
        Assert.False(fresh.IsUpdating);
        Assert.False(fresh.UpdateFailed);
        Assert.NotNull(fresh.LatestObservationAt);
        Assert.NotNull(fresh.LastSuccessfulCaptureAt);
        Assert.NotNull(fresh.CacheExpiresAt);
    }

    [Fact]
    public async Task Failed_capture_does_not_make_health_unhealthy_and_failure_is_visible()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.Fail;

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatus(client);
            return status.UpdateFailed;
        });

        var failed = await GetStatus(client);
        Assert.Equal("SCRAPING_ERROR", failed.ErrorCode);
        Assert.True(failed.ConsecutiveFailures >= 1);
        Assert.NotNull(failed.NextRetryAt);

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }


    [Fact]
    public async Task Clock_controls_expiry_without_wall_clock_sleeps()
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-1),
            now,
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-35 - index * 0 + index * 5)).ToArray());

        using var client = app.CreateClient();
        var fresh = await GetStatus(client);
        Assert.True(fresh.CacheIsValid);

        app.Clock.Advance(TimeSpan.FromMinutes(13));
        var stale = await GetStatus(client);
        Assert.False(stale.CacheIsValid);
        Assert.Equal("Cache is stale", stale.Message);
    }

    private static async Task<CacheUpdateStatus> GetStatus(HttpClient client)
    {
        using var response = await client.GetAsync("/api/cache/Pomona/QLD/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<CacheUpdateStatus>(
            await response.Content.ReadAsStringAsync(), TestJson.Options)!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Condition was not met within 10 seconds.");
    }
}
