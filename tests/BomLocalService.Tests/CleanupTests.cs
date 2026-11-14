using System.Net;
using BomLocalService.Services.Interfaces;
using BomLocalService.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BomLocalService.Tests;

public class CleanupTests
{
    [Fact]
    public async Task Startup_leaves_unknown_directories_and_removes_incomplete_recognised_folders()
    {
        await using var app = new BomServiceFactory();
        var unknown = Path.Combine(app.CacheDirectory, "not-a-cache");
        Directory.CreateDirectory(unknown);
        var sentinel = TestCache.CreateSentinel(unknown);

        var incomplete = Path.Combine(app.CacheDirectory, "Pomona_QLD_20261111_000000");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "metadata.json"), "{}");

        using var client = app.CreateClient();

        Assert.True(Directory.Exists(unknown));
        Assert.True(File.Exists(sentinel));
        Assert.False(Directory.Exists(incomplete));
    }

    [Fact]
    public async Task Null_frame_elements_do_not_crash_inventory_or_startup_cleanup()
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        var folder = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray());

        File.WriteAllText(
            Path.Combine(folder, "radar", "frames.json"),
            "[" + string.Join(",", Enumerable.Repeat("null", 7)) + "]");

        using var client = app.CreateClient();
        var cache = app.Services.GetRequiredService<ICacheService>();
        var entries = cache.GetCacheInventory();
        Assert.DoesNotContain(entries, entry => entry.FolderName == Path.GetFileName(folder));

        using var range = await client.GetAsync("/api/cache/Pomona/QLD/range");
        Assert.NotEqual(HttpStatusCode.InternalServerError, range.StatusCode);
    }

    [Fact]
    public async Task Retention_preserves_latest_complete_fallback_and_skips_unknown_directories()
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        var observations = Enumerable.Range(0, 7).Select(index => now.AddHours(-48).AddMinutes(index * 5)).ToArray();

        var oldest = TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddHours(-48), now.AddHours(-47), observations);
        var latest = TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddHours(-47), now.AddHours(-46), observations);
        var onlySydney = TestCache.SeedSnapshot(app.CacheDirectory, "Sydney", "NSW", now.AddHours(-49), now.AddHours(-48), observations);

        var unknown = Path.Combine(app.CacheDirectory, "retain-me");
        Directory.CreateDirectory(unknown);
        var sentinel = TestCache.CreateSentinel(unknown);

        using var client = app.CreateClient();
        var cleanup = app.CreateCleanupService();
        await cleanup.RunCleanupAsync();

        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(latest));
        Assert.True(Directory.Exists(onlySydney));
        Assert.True(Directory.Exists(unknown));
        Assert.True(File.Exists(sentinel));
    }
}
