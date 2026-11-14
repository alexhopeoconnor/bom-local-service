using System.Net;
using System.Reflection;
using BomLocalService.Models;
using BomLocalService.Services;
using BomLocalService.Services.Interfaces;
using BomLocalService.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BomLocalService.Tests;

public class CacheDeleteTests
{
    [Fact]
    public async Task Delete_keeps_monitoring_state_for_a_legacy_only_location()
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        var folder = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray(),
            completeMarker: false);

        using var client = app.CreateClient();
        using var response = await client.DeleteAsync("/api/cache/Pomona/QLD");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(folder));

        var store = app.Services.GetRequiredService<ILocationStateStore>();
        var state = await store.GetAsync(LocationId.Create("Pomona", "QLD"));
        Assert.NotNull(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_preserves_state_restart_discovery_and_recaptures(bool existingDurableState)
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        var location = LocationId.Create("Pomona", "QLD");
        var priorAttempt = now.AddMinutes(-30);
        var priorSuccess = now.AddHours(-2);
        var priorObservation = priorSuccess.AddMinutes(5);
        var priorRetry = now.AddMinutes(-5);

        var seedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheDirectory"] = app.CacheDirectory
            })
            .Build();

        if (existingDurableState)
        {
            var seedStore = new LocationStateStore(NullLogger<LocationStateStore>.Instance, seedConfiguration);
            Assert.True(await seedStore.SaveAsync(new LocationRuntimeState
            {
                Suburb = location.Suburb,
                State = location.State,
                LastAttemptAt = priorAttempt,
                LastSuccessfulCaptureAt = priorSuccess,
                LatestObservationAt = priorObservation,
                NextRetryAt = priorRetry,
                UpdateFailed = true,
                ErrorCode = "SCRAPING_ERROR",
                Error = "prior failure",
                ConsecutiveFailures = 3,
                UpdatedAt = priorAttempt
            }));
        }

        var folder = TestCache.SeedSnapshot(
            app.CacheDirectory,
            location.Suburb,
            location.State,
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray(),
            completeMarker: false);

        using var client = app.CreateClient();
        using var deleted = await client.DeleteAsync("/api/cache/Pomona/QLD");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(Directory.Exists(folder));

        // A fresh store instance is the restart boundary for durable state.
        var restartedStore = new LocationStateStore(
            NullLogger<LocationStateStore>.Instance,
            app.Services.GetRequiredService<IConfiguration>());
        var reloaded = await restartedStore.GetAsync(location);
        Assert.NotNull(reloaded);
        var durable = reloaded!;
        Assert.Equal(existingDurableState ? 3 : 0, durable.ConsecutiveFailures);

        if (existingDurableState)
        {
            Assert.Equal(priorAttempt, durable.LastAttemptAt);
            Assert.Equal(priorSuccess, durable.LastSuccessfulCaptureAt);
            Assert.Equal(priorObservation, durable.LatestObservationAt);
            Assert.Equal(priorRetry, durable.NextRetryAt);
            Assert.True(durable.UpdateFailed);
            Assert.Equal("SCRAPING_ERROR", durable.ErrorCode);
            Assert.Equal("prior failure", durable.Error);
        }
        else
        {
            Assert.False(durable.UpdateFailed);
            Assert.Null(durable.LastAttemptAt);
            Assert.Null(durable.LastSuccessfulCaptureAt);
            Assert.Null(durable.LatestObservationAt);
            Assert.Null(durable.NextRetryAt);
        }

        // Management discovery after restart must still see the location even
        // though its legacy folder was deleted and inventory is empty.
        var cache = app.Services.GetRequiredService<ICacheService>();
        Assert.DoesNotContain(
            cache.GetCacheInventory(),
            entry => entry.Location.Key == location.Key);

        var management = new CacheManagementService(
            app.Services.GetRequiredService<ILogger<CacheManagementService>>(),
            app.Services.GetRequiredService<IBomRadarService>(),
            app.Services.GetRequiredService<ICacheService>(),
            restartedStore,
            app.Services.GetRequiredService<ICacheRefreshQueue>(),
            app.Services.GetRequiredService<IBrowserService>(),
            app.Services.GetRequiredService<IConfiguration>());
        var method = typeof(CacheManagementService).GetMethod(
            "GetLocationsToManageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<List<LocationId>>)method.Invoke(
            management,
            new object[] { CancellationToken.None })!;
        var managed = await task;
        Assert.Contains(managed, candidate => candidate.Key == location.Key);

        // A permitted refresh must recapture the now-missing snapshot.
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        await WaitUntilAsync(
            () => Task.FromResult(
                app.Capture.CallsFor(location) >= 1 &&
                cache.GetCacheInventory().Any(entry =>
                    entry.Location.Key == location.Key &&
                    entry.IsComplete)),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Delete_storage_failure_leaves_snapshots_and_releases_the_reservation()
    {
        var store = new ControllableLocationStateStore { SaveResult = false };
        await using var app = new BomServiceFactory(stateStore: store);
        var now = app.Clock.UtcNow;
        var folder = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray(),
            completeMarker: false);
        var before = TestCache.SnapshotHashes(app.CacheDirectory);

        using var client = app.CreateClient();
        using var response = await client.DeleteAsync("/api/cache/Pomona/QLD");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(before, TestCache.SnapshotHashes(app.CacheDirectory));
        Assert.True(store.SaveCalls > 0);

        store.SaveResult = true;
        using var refresh = await client.PostAsync("/api/cache/Pomona/QLD/refresh", null);
        Assert.NotEqual(HttpStatusCode.Conflict, refresh.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
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
