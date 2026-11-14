using BomLocalService.Models;
using BomLocalService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BomLocalService.Tests;

public class LocationStateStoreTests
{
    [Fact]
    public async Task Save_persists_a_defensive_copy_and_get_returns_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CacheDirectory"] = root
                })
                .Build();
            var store = new LocationStateStore(NullLogger<LocationStateStore>.Instance, configuration);
            var location = LocationId.Create("Pomona", "QLD");
            var updatedAt = new DateTime(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);
            var state = new LocationRuntimeState
            {
                Suburb = location.Suburb,
                State = location.State,
                NextRetryAt = updatedAt.AddMinutes(5),
                ConsecutiveFailures = 3,
                UpdatedAt = updatedAt
            };

            Assert.True(await store.SaveAsync(state));
            state.ConsecutiveFailures = 99;

            var loaded = await store.GetAsync(location);
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.ConsecutiveFailures);
            Assert.Equal(updatedAt, loaded.UpdatedAt);
            Assert.Equal(updatedAt.AddMinutes(5), loaded.NextRetryAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Replaced_service_ancestor_fails_closed_without_writing_outside()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-state-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "bom-state-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(outside, "locations"));
        var sentinel = Path.Combine(outside, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "untouched");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CacheDirectory"] = root
                })
                .Build();
            var store = new LocationStateStore(NullLogger<LocationStateStore>.Instance, configuration);
            var location = LocationId.Create("Pomona", "QLD");
            var servicePath = Path.Combine(root, ".service");

            try
            {
                Directory.Delete(servicePath, recursive: true);
                Directory.CreateSymbolicLink(servicePath, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Empty(await store.GetAllAsync());
            Assert.Null(await store.GetAsync(location));
            Assert.False(await store.SaveAsync(new LocationRuntimeState
            {
                Suburb = location.Suburb,
                State = location.State,
                UpdatedAt = DateTime.UtcNow
            }));

            Assert.True(File.Exists(sentinel));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.Empty(Directory.GetFiles(Path.Combine(outside, "locations"), "*.json"));
        }
        finally
        {
            var servicePath = Path.Combine(root, ".service");
            if (Directory.Exists(servicePath))
            {
                Directory.Delete(servicePath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetAsync_propagates_caller_cancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CacheDirectory"] = root
                })
                .Build();
            var store = new LocationStateStore(NullLogger<LocationStateStore>.Instance, configuration);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => store.GetAsync(LocationId.Create("Pomona", "QLD"), cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupt_state_is_ignored_without_failing_get_all()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CacheDirectory"] = root
                })
                .Build();
            var store = new LocationStateStore(NullLogger<LocationStateStore>.Instance, configuration);
            var location = LocationId.Create("Pomona", "QLD");
            Assert.True(await store.SaveAsync(new LocationRuntimeState
            {
                Suburb = location.Suburb,
                State = location.State,
                UpdatedAt = DateTime.UtcNow
            }));

            var stateFile = Directory.GetFiles(Path.Combine(root, ".service", "locations"), "*.json").Single();
            await File.WriteAllTextAsync(stateFile, "{not-json");

            var all = await store.GetAllAsync();
            Assert.Empty(all);
            Assert.Null(await store.GetAsync(location));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
