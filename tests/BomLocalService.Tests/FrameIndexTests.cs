using System.Net;
using BomLocalService.Tests.Infrastructure;
using Xunit;

namespace BomLocalService.Tests;

public class FrameIndexTests
{
    [Fact]
    public async Task Frame_indices_follow_configuration_without_hard_coded_range()
    {
        await using var app = new BomServiceFactory(settings =>
            settings["CachedDataTypes:Radar:FrameCount"] = "5");
        var now = app.Clock.UtcNow;
        var observations = Enumerable.Range(0, 5).Select(index => now.AddMinutes(-25 + index * 5)).ToArray();
        var folder = TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddMinutes(-5), now, observations);

        using var client = app.CreateClient();

        using var last = await client.GetAsync("/api/radar/Pomona/QLD/frame/4");
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);

        using var outOfRange = await client.GetAsync("/api/radar/Pomona/QLD/frame/5");
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        using var negative = await client.GetAsync("/api/radar/Pomona/QLD/frame/-1");
        Assert.NotEqual(HttpStatusCode.OK, negative.StatusCode);

        using var historical = await client.GetAsync(
            $"/api/radar/Pomona/QLD/frame/4?cacheFolder={Uri.EscapeDataString(Path.GetFileName(folder))}");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);

        using var historicalOutOfRange = await client.GetAsync(
            $"/api/radar/Pomona/QLD/frame/5?cacheFolder={Uri.EscapeDataString(Path.GetFileName(folder))}");
        Assert.Equal(HttpStatusCode.BadRequest, historicalOutOfRange.StatusCode);
    }
}
