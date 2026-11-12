using System.Net;
using System.Text.Json;
using BomLocalService.Services;
using BomLocalService.Utilities;
using BomLocalService.Tests.Infrastructure;
using Xunit;

namespace BomLocalService.Tests;

public class CacheIsolationTests
{
    [Theory]
    [InlineData("*", "QLD")]
    [InlineData("Brisbane", "*")]
    [InlineData("Brisbane", "XX")]
    public async Task Invalid_location_cannot_delete_cache(string suburb, string state)
    {
        await using var app = new BomServiceFactory();
        SeedBrisbaneAndSydney(app);
        var before = TestCache.SnapshotHashes(app.CacheDirectory);

        using var client = app.CreateClient();
        using var response = await client.DeleteAsync(
            $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, TestCache.SnapshotHashes(app.CacheDirectory));
    }

    [Fact]
    public async Task Delete_rejects_folder_syntax_and_wrong_location_without_touching_outside_sentinel()
    {
        await using var app = new BomServiceFactory();
        var brisbane = SeedBrisbaneAndSydney(app);
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "bom-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var sentinel = TestCache.CreateSentinel(outsideDirectory);

        try
        {
            using var client = app.CreateClient();

            using var traversal = await client.GetAsync(
                "/api/radar/Brisbane/QLD/frame/0?cacheFolder=" + Uri.EscapeDataString("../Brisbane_QLD_20261112_000000"));
            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

            using var absolute = await client.GetAsync(
                "/api/radar/Brisbane/QLD/frame/0?cacheFolder=" + Uri.EscapeDataString("/tmp/anything"));
            Assert.Equal(HttpStatusCode.BadRequest, absolute.StatusCode);

            using var wrongLocation = await client.GetAsync(
                $"/api/radar/Brisbane/QLD/frame/0?cacheFolder={Uri.EscapeDataString(Path.GetFileName(brisbane))}");
            // Brisbane's own folder is valid; Sydney's folder must be a 404 for Brisbane.
            Assert.Equal(HttpStatusCode.OK, wrongLocation.StatusCode);

            var sydneyFolder = Directory.GetDirectories(app.CacheDirectory)
                .Single(path => Path.GetFileName(path).StartsWith("Sydney_NSW_", StringComparison.Ordinal));
            using var foreignFolder = await client.GetAsync(
                $"/api/radar/Brisbane/QLD/frame/0?cacheFolder={Uri.EscapeDataString(Path.GetFileName(sydneyFolder))}");
            Assert.Equal(HttpStatusCode.NotFound, foreignFolder.StatusCode);

            using var delete = await client.DeleteAsync("/api/cache/Brisbane/QLD");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

            Assert.True(File.Exists(sentinel));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Recognised_symlink_folder_is_ignored_and_outside_data_survives()
    {
        await using var app = new BomServiceFactory();
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "bom-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var sentinel = TestCache.CreateSentinel(outsideDirectory);

        var linkName = "Pomona_QLD_20261112_000000";
        var linkPath = Path.Combine(app.CacheDirectory, linkName);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // The platform cannot create symlinks in this test environment.
            }

            using var client = app.CreateClient();
            using var response = await client.GetAsync("/api/cache/Pomona/QLD/range");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            using var delete = await client.DeleteAsync("/api/cache/Pomona/QLD");
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

            Assert.True(File.Exists(sentinel));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(outsideDirectory));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void Folder_resolver_rejects_absolute_paths_separators_wildcards_and_parent_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Pomona_QLD_20261112_000000"));

        try
        {
            foreach (var candidate in new[]
                     {
                         "../Pomona_QLD_20261112_000000",
                         "..\\Pomona_QLD_20261112_000000",
                         "/tmp",
                         "C:\\temp",
                         "Pomona*",
                         "Pomona?",
                         ".",
                         "..",
                         "a/b"
                     })
            {
                Assert.False(FilePathHelper.TryResolveDirectChild(root, candidate, out _, out _), candidate);
            }

            Assert.True(FilePathHelper.TryResolveDirectChild(
                root,
                "Pomona_QLD_20261112_000000",
                out var resolved,
                out var error), error);
            Assert.Equal(Path.Combine(root, "Pomona_QLD_20261112_000000"), resolved!.FullPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SeedBrisbaneAndSydney(BomServiceFactory app)
    {
        var now = app.Clock.UtcNow;
        var brisbane = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Brisbane",
            "QLD",
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray());

        TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Sydney",
            "NSW",
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            Enumerable.Range(0, 7).Select(index => now.AddMinutes(-40 + index * 5)).ToArray());

        return brisbane;
    }
}
