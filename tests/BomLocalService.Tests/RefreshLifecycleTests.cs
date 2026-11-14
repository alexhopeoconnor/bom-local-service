using System.Net;
using System.Reflection;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Services;
using BomLocalService.Tests.Infrastructure;
using BomLocalService.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BomLocalService.Tests;

public class RefreshLifecycleTests
{
    [Fact]
    public async Task Failed_capture_keeps_previous_snapshot_and_staging_is_removed()
    {
        var observation = DateTime.UtcNow.AddMinutes(-30);
        await using var app = new BomServiceFactory();
        var previous = SeedPreviousSnapshot(app, observation);
        app.Capture.Behavior = CaptureBehavior.Fail;

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatus(client, "Pomona", "QLD");
            return status?.UpdateFailed == true;
        }, TimeSpan.FromSeconds(10));

        var failedStatus = await GetStatus(client, "Pomona", "QLD");
        Assert.NotNull(failedStatus);
        Assert.Equal("SCRAPING_ERROR", failedStatus!.ErrorCode);
        Assert.Equal(1, failedStatus.ConsecutiveFailures);
        Assert.NotNull(failedStatus.NextRetryAt);
        Assert.False(failedStatus.CacheIsValid);

        var radar = await GetRadar(client, "Pomona", "QLD");
        Assert.Equal(HttpStatusCode.OK, radar.StatusCode);
        var body = await ReadRadar(radar);
        Assert.All(body.Frames, frame => Assert.Contains(Path.GetFileName(previous), Uri.UnescapeDataString(frame.ImageUrl)));
        Assert.Equal(observation, body.ObservationTime);

        Assert.Empty(Directory.GetDirectories(Path.Combine(app.CacheDirectory, ".staging")));
        Assert.Single(Directory.GetDirectories(app.CacheDirectory).Where(path => Path.GetFileName(path).StartsWith("Pomona_QLD_", StringComparison.Ordinal)));

        // A first-time failure remains in retry tracking even without a cache folder.
        var stateFiles = Directory.GetFiles(Path.Combine(app.CacheDirectory, ".service", "locations"), "*.json");
        Assert.Single(stateFiles);
        Assert.Contains("SCRAPING_ERROR", await File.ReadAllTextAsync(stateFiles[0]));

        // Backoff cannot be bypassed by an immediate manual refresh.
        using var rejected = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, app.Capture.TotalCalls);

        app.Clock.Advance(TimeSpan.FromSeconds(61));
        app.Capture.Behavior = CaptureBehavior.Succeed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-2);
        app.Capture.ResetSignals();

        using var retry = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.Capture.ReleaseSignal.TrySetResult(true);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatus(client, "Pomona", "QLD");
            return status is { UpdateFailed: false, ConsecutiveFailures: 0, LastSuccessfulCaptureAt: not null };
        }, TimeSpan.FromSeconds(10));

        var recovered = await GetStatus(client, "Pomona", "QLD");
        Assert.Null(recovered!.ErrorCode);
        Assert.Null(recovered.NextRetryAt);
        Assert.True(recovered.CacheIsValid);
    }

    [Fact]
    public async Task Previous_snapshot_is_served_while_capture_is_paused_and_new_one_appears_after_publish()
    {
        var oldObservation = DateTime.UtcNow.AddMinutes(-40);
        await using var app = new BomServiceFactory();
        var previous = SeedPreviousSnapshot(app, oldObservation);
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-5);
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var during = await GetRadar(client, "Pomona", "QLD");
        Assert.Equal(HttpStatusCode.OK, during.StatusCode);
        var duringBody = await ReadRadar(during);
        Assert.All(duringBody.Frames, frame => Assert.Contains(Path.GetFileName(previous), Uri.UnescapeDataString(frame.ImageUrl)));
        Assert.Equal(oldObservation, duringBody.ObservationTime);

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () =>
        {
            var response = await GetRadar(client, "Pomona", "QLD");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var body = await ReadRadar(response);
            return body.ObservationTime > oldObservation;
        }, TimeSpan.FromSeconds(10));

        var after = await ReadRadar(await GetRadar(client, "Pomona", "QLD"));
        Assert.DoesNotContain(Path.GetFileName(previous), Uri.UnescapeDataString(after.Frames[0].ImageUrl));
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.CacheDirectory, ".staging")));
    }

    [Fact]
    public async Task Abandoned_staging_cleanup_refuses_nested_symlink_and_preserves_outside_data()
    {
        await using var app = new BomServiceFactory();
        var staging = Path.Combine(app.CacheDirectory, FilePathHelper.StagingDirectoryName, "Pomona_QLD_20261112_000000");
        Directory.CreateDirectory(staging);

        var outsideDirectory = Path.Combine(Path.GetTempPath(), "bom-staging-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var sentinel = TestCache.CreateSentinel(outsideDirectory);
        var linkPath = Path.Combine(staging, "radar");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var refreshService = app.Services.GetRequiredService<CacheRefreshService>();
            var method = typeof(CacheRefreshService).GetMethod(
                "DeleteAbandonedStagingAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            await (Task)method.Invoke(refreshService, new object[] { staging })!;

            Assert.True(File.Exists(sentinel));
            Assert.True(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Incomplete_capture_inside_cache_root_is_never_visible()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        using var client = app.CreateClient();

        var now = app.Clock.UtcNow;
        var folderPath = Path.Combine(app.CacheDirectory, $"Pomona_QLD_{now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(Path.Combine(folderPath, "radar"));
        File.WriteAllText(Path.Combine(folderPath, "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(folderPath, "radar", "frames.json"), "[]");

        var response = await GetRadar(client, "Pomona", "QLD");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var frame = await client.GetAsync(
            $"/api/radar/Pomona/QLD/frame/0?cacheFolder={Uri.EscapeDataString(Path.GetFileName(folderPath))}");
        Assert.Equal(HttpStatusCode.NotFound, frame.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_requests_for_one_location_cause_one_capture()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-5);
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var first = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        using var second = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, app.Capture.CallsFor(LocationId.Create("Pomona", "QLD")));

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () =>
            (await GetRadar(client, "Pomona", "QLD")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(10));
        Assert.Equal(1, app.Capture.TotalCalls);
    }

    [Fact]
    public async Task Queue_and_concurrency_limits_are_enforced()
    {
        await using var app = new BomServiceFactory(settings =>
        {
            settings["Refresh:QueueCapacity"] = "1";
            settings["Refresh:MaxConcurrentCaptures"] = "1";
        });
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var first = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var second = await client.PostAsync("/api/cache/Sydney/NSW/refresh", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var third = await client.PostAsync("/api/cache/Brisbane/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var thirdStatus = JsonSerializer.Deserialize<CacheUpdateStatus>(await third.Content.ReadAsStringAsync(), TestJson.Options);
        Assert.Equal("RejectedQueueFull", thirdStatus!.RejectionCode);

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () => app.Capture.TotalCalls == 2, TimeSpan.FromSeconds(10));
        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () => app.Capture.TotalCalls >= 2, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Multiple_locations_respect_configured_concurrency_limit()
    {
        await using var app = new BomServiceFactory(settings =>
        {
            settings["Refresh:QueueCapacity"] = "8";
            settings["Refresh:MaxConcurrentCaptures"] = "2";
        });
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var first = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        using var second = await client.PostAsync("/api/cache/Sydney/NSW/refresh", null);
        using var third = await client.PostAsync("/api/cache/Brisbane/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        await WaitUntilAsync(() => Task.FromResult(app.Capture.Active == 2), TimeSpan.FromSeconds(5));
        Assert.Equal(2, app.Capture.Active);
        Assert.Equal(2, app.Capture.TotalCalls);

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(() => Task.FromResult(app.Capture.TotalCalls >= 3), TimeSpan.FromSeconds(10));
        Assert.True(app.Capture.MaxConcurrent <= 2, $"max concurrency was {app.Capture.MaxConcurrent}");
    }

    [Fact]
    public async Task Deletion_during_running_capture_returns_conflict()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var conflict = await client.DeleteAsync("/api/cache/Pomona/QLD");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () =>
            (await GetRadar(client, "Pomona", "QLD")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(10));

        using var deleted = await client.DeleteAsync("/api/cache/Pomona/QLD");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Accepted_refresh_survives_client_response_completion()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-5);
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var initial = await GetRadar(client, "Pomona", "QLD");
        Assert.Equal(HttpStatusCode.NotFound, initial.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () =>
            (await GetRadar(client, "Pomona", "QLD")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Shutdown_cancels_in_flight_capture_and_leaves_no_staging_work()
    {
        var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.WaitForCancellation;
        app.Capture.ResetSignals();

        using (var client = app.CreateClient())
        {
            using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await app.DisposeAsync();
        await app.Capture.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stagingRoot = Path.Combine(app.CacheDirectory, ".staging");
        Assert.True(!Directory.Exists(stagingRoot) || !Directory.EnumerateDirectories(stagingRoot).Any());
    }


    [Fact]
    public async Task Capture_deadline_records_timeout_and_releases_worker()
    {
        await using var app = new BomServiceFactory(settings => settings["Refresh:CaptureTimeoutSeconds"] = "1");
        app.Capture.Behavior = CaptureBehavior.WaitForCancellation;
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatus(client, "Pomona", "QLD");
            // The worker releases its reservation only after the finally block
            // has attempted abandoned-staging cleanup.
            return status is
            {
                UpdateFailed: true,
                ErrorCode: "TIMEOUT_ERROR",
                IsUpdating: false
            };
        }, TimeSpan.FromSeconds(10));

        var status = await GetStatus(client, "Pomona", "QLD");
        Assert.Equal(1, status!.ConsecutiveFailures);
        Assert.NotNull(status.NextRetryAt);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.CacheDirectory, ".staging")));

        app.Capture.Behavior = CaptureBehavior.Succeed;
        app.Capture.ResetSignals();
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        using var retry = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.Capture.ReleaseSignal.TrySetResult(true);
        await WaitUntilAsync(async () =>
            (await GetRadar(client, "Pomona", "QLD")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Browser_error_is_reported_and_clears_after_recovery()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.BrowserFail;

        using var client = app.CreateClient();
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(async () =>
        {
            var status = await GetStatus(client, "Pomona", "QLD");
            return status?.ErrorCode == "BROWSER_ERROR";
        }, TimeSpan.FromSeconds(10));

        app.Capture.Behavior = CaptureBehavior.Succeed;
        app.Capture.ObservationTime = DateTime.UtcNow.AddMinutes(-5);
        app.Capture.ResetSignals();
        app.Clock.Advance(TimeSpan.FromSeconds(61));

        using var retry = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await app.Capture.StartSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.Capture.ReleaseSignal.TrySetResult(true);

        await WaitUntilAsync(async () =>
            (await GetRadar(client, "Pomona", "QLD")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(10));

        var recovered = await GetStatus(client, "Pomona", "QLD");
        Assert.False(recovered!.UpdateFailed);
        Assert.Null(recovered.ErrorCode);
    }

    private static string SeedPreviousSnapshot(BomServiceFactory app, DateTime observation)
    {
        return TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            observation.AddMinutes(-5),
            observation,
            Enumerable.Range(0, 7).Select(index => observation.AddMinutes(-35 + index * 5)).ToArray());
    }

    private static async Task<HttpResponseMessage> GetRadar(HttpClient client, string suburb, string state) =>
        await client.GetAsync($"/api/radar/{suburb}/{state}");

    private static async Task<RadarResponse> ReadRadar(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<RadarResponse>(await response.Content.ReadAsStringAsync(), TestJson.Options)!;

    private static async Task<CacheUpdateStatus?> GetStatus(HttpClient client, string suburb, string state)
    {
        using var response = await client.GetAsync($"/api/cache/{suburb}/{state}/status");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonSerializer.Deserialize<CacheUpdateStatus>(await response.Content.ReadAsStringAsync(), TestJson.Options);
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
