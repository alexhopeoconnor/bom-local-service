using System.Net;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Tests.Infrastructure;
using Xunit;

namespace BomLocalService.Tests;

public class LegacyCompatibilityTests
{
    [Fact]
    public async Task Pre_change_cache_fixture_remains_readable_without_a_completion_marker()
    {
        await using var app = new BomServiceFactory();
        // The synthetic legacy fixture uses 1x1 RGBA PNGs with valid CRCs and
        // zlib data. Validation must not be relaxed for invalid image fixtures.
        CopyFixture(app, "Pomona_QLD_20251207_000906");
        app.Capture.Behavior = CaptureBehavior.PauseThenSucceed;
        app.Capture.ResetSignals();

        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/radar/Pomona/QLD");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var radar = JsonSerializer.Deserialize<RadarResponse>(await response.Content.ReadAsStringAsync(), TestJson.Options)!;
        Assert.Equal(7, radar.Frames.Count);
        Assert.Equal(new DateTime(2025, 12, 7, 0, 5, 0, DateTimeKind.Utc), radar.ObservationTime);
        Assert.Equal("Legacy Station", radar.WeatherStation);
        Assert.All(radar.Frames, frame => Assert.Contains("cacheFolder=", frame.ImageUrl));

        using var latestRouteFrame = await client.GetAsync("/api/radar/Pomona/QLD/frame/6");
        Assert.Equal(HttpStatusCode.OK, latestRouteFrame.StatusCode);

        using var frame = await client.GetAsync(radar.Frames[6].ImageUrl);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        var bytes = await frame.Content.ReadAsByteArrayAsync();
        var expected = await File.ReadAllBytesAsync(Path.Combine(
            app.CacheDirectory,
            "Pomona_QLD_20251207_000906",
            "radar",
            "frame_6.png"));
        Assert.Equal(expected, bytes);

        using var history = await client.GetAsync(
            "/api/radar/Pomona/QLD/timeseries?startTime=2025-12-06T23%3A35%3A00Z&endTime=2025-12-06T23%3A45%3A00Z");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var series = JsonSerializer.Deserialize<RadarTimeSeriesResponse>(
            await history.Content.ReadAsStringAsync(), TestJson.Options)!;
        Assert.Equal(3, series.TotalFrames);
        Assert.All(series.CacheFolders, folder => Assert.Equal("Pomona_QLD_20251207_000906", folder.CacheFolderName));
    }

    [Fact]
    public async Task Mixed_case_multiword_dotted_apostrophe_and_hyphen_names_resolve()
    {
        await using var app = new BomServiceFactory();
        var now = app.Clock.UtcNow;
        var observations = Enumerable.Range(0, 7).Select(index => now.AddMinutes(-35 + index * 5)).ToArray();

        TestCache.SeedSnapshot(app.CacheDirectory, "St. Lucia", "QLD", now.AddMinutes(-5), now, observations,
            folderName: "St._Lucia_QLD_" + now.AddMinutes(-5).ToString("yyyyMMdd_HHmmss"));
        TestCache.SeedSnapshot(app.CacheDirectory, "O'Connell", "NSW", now.AddMinutes(-5), now, observations);
        TestCache.SeedSnapshot(app.CacheDirectory, "Wagga Wagga", "NSW", now.AddMinutes(-5), now, observations);
        TestCache.SeedSnapshot(app.CacheDirectory, "Yéppoon", "QLD", now.AddMinutes(-5), now, observations);

        using var client = app.CreateClient();

        foreach (var (suburb, state) in new[]
                 {
                     ("st. lucia", "qld"),
                     ("O'Connell", "nsw"),
                     ("Wagga Wagga", "NSW"),
                     ("Yéppoon", "QLD")
                 })
        {
            using var response = await client.GetAsync($"/api/radar/{Uri.EscapeDataString(suburb)}/{state}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static void CopyFixture(BomServiceFactory app, string folderName)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-cache", folderName);
        var target = Path.Combine(app.CacheDirectory, folderName);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
