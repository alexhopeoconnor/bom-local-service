using System.Net;
using System.Text.Json;
using BomLocalService.Tests.Infrastructure;
using Xunit;

namespace BomLocalService.Tests;

public class HistoricalSelectionTests
{
    [Theory]
    [InlineData("not-a-date", null, "startTime")]
    [InlineData(null, "not-a-date", "endTime")]
    public async Task Malformed_bounds_return_top_level_validation_errors(
        string? start, string? end, string field)
    {
        await using var app = new BomServiceFactory();
        using var client = app.CreateClient();
        using var response = await GetTimeSeriesRaw(client, "Pomona", "QLD", start, end);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(json.RootElement.TryGetProperty("value", out _));
        Assert.Contains(field, json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Observation_bounds_are_inclusive_and_not_limited_by_folder_capture_time()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "6");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));
        var observations = Enumerable.Range(0, 6).Select(i => baseTime.AddMinutes(i * 5)).ToArray();

        // Capture timestamp is intentionally well before the requested observation window.
        TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddHours(-3), observations[^1].AddMinutes(5), observations);

        using var client = app.CreateClient();

        var late = await GetTimeSeries(client, "Pomona", "QLD", observations[4], observations[5].AddMinutes(5));
        Assert.Equal(2, late.TotalFrames);
        Assert.Equal(new[] { observations[4], observations[5] }, FlattenObservations(late));

        var early = await GetTimeSeries(client, "Pomona", "QLD", observations[0], observations[2]);
        Assert.Equal(3, early.TotalFrames);
        Assert.Equal(new[] { observations[0], observations[1], observations[2] }, FlattenObservations(early));
    }

    [Fact]
    public async Task Duplicate_observations_choose_newest_capture_and_keep_origin_frame_bytes()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "3");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));

        var older = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-40),
            baseTime.AddMinutes(15),
            new[] { baseTime, baseTime.AddMinutes(5), baseTime.AddMinutes(10) },
            weatherStation: "Older Station");

        var newer = TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-20),
            baseTime.AddMinutes(20),
            new[] { baseTime.AddMinutes(5), baseTime.AddMinutes(10), baseTime.AddMinutes(15) },
            weatherStation: "Newer Station");

        using var client = app.CreateClient();
        var result = await GetTimeSeries(client, "Pomona", "QLD", baseTime, baseTime.AddMinutes(20));

        Assert.Equal(4, result.TotalFrames);
        Assert.Equal(
            new[]
            {
                baseTime,
                baseTime.AddMinutes(5),
                baseTime.AddMinutes(10),
                baseTime.AddMinutes(15)
            },
            FlattenObservations(result));

        var duplicateFrames = result.CacheFolders
            .SelectMany(folder => folder.Frames.Select(frame => (folder, frame)))
            .Where(x => x.frame.AbsoluteObservationTime is { } value &&
                        (value == baseTime.AddMinutes(5) || value == baseTime.AddMinutes(10)))
            .ToList();

        Assert.Equal(2, duplicateFrames.Count);
        Assert.All(duplicateFrames, item => Assert.StartsWith(Path.GetFileName(newer), item.folder.CacheFolderName));

        foreach (var (folder, frame) in duplicateFrames)
        {
            using var response = await client.GetAsync(frame.ImageUrl);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var expected = await File.ReadAllBytesAsync(Path.Combine(app.CacheDirectory, folder.CacheFolderName, "radar", $"frame_{frame.FrameIndex}.png"));
            Assert.Equal(expected, bytes);
        }

        // The single non-duplicated older observation keeps its correct origin.
        var oldest = result.CacheFolders.SelectMany(f => f.Frames).Single(f => f.AbsoluteObservationTime == baseTime);
        using var olderResponse = await client.GetAsync(oldest.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, olderResponse.StatusCode);
        Assert.Contains(Path.GetFileName(older), Uri.UnescapeDataString(oldest.ImageUrl));
    }

    [Fact]
    public async Task Explicit_timezone_offsets_and_offsetless_legacy_values_are_compared_in_utc()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "4");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));
        var observations = Enumerable.Range(0, 4).Select(i => baseTime.AddMinutes(i * 5)).ToArray();
        TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddMinutes(-10), observations[^1].AddMinutes(5), observations);

        using var client = app.CreateClient();

        var offsetStart = baseTime.AddMinutes(5).AddHours(10).ToString("yyyy-MM-ddTHH:mm:ss+10:00");
        var offsetEnd = baseTime.AddMinutes(10).AddHours(10).ToString("yyyy-MM-ddTHH:mm:ss+10:00");
        var withOffset = await GetTimeSeriesRaw(client, "Pomona", "QLD", offsetStart, offsetEnd);
        Assert.Equal(HttpStatusCode.OK, withOffset.StatusCode);
        Assert.Equal(
            new[] { observations[1], observations[2] },
            FlattenObservations(await ReadTimeSeries(withOffset)));

        // A value without an offset is interpreted as UTC, preserving legacy clients.
        var legacy = await GetTimeSeriesRaw(
            client,
            "Pomona",
            "QLD",
            baseTime.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss"),
            baseTime.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss"));
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(
            new[] { observations[1], observations[2] },
            FlattenObservations(await ReadTimeSeries(legacy)));
    }

    [Fact]
    public async Task Reversed_range_is_structured_400_and_optional_bounds_are_preserved()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "3");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));
        var observations = Enumerable.Range(0, 3).Select(i => baseTime.AddMinutes(i * 5)).ToArray();
        TestCache.SeedSnapshot(app.CacheDirectory, "Pomona", "QLD", now.AddMinutes(-10), observations[^1].AddMinutes(5), observations);

        using var client = app.CreateClient();

        var reversed = await GetTimeSeriesRaw(client, "Pomona", "QLD", observations[2], observations[0]);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        using var reversedJson = JsonDocument.Parse(await reversed.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", reversedJson.RootElement.GetProperty("errorCode").GetString());

        var all = await GetTimeSeries(client, "Pomona", "QLD", null, null);
        Assert.Equal(3, all.TotalFrames);
        Assert.Null(all.StartTime);
        Assert.Null(all.EndTime);
    }

    [Fact]
    public async Task Empty_in_range_result_preserves_structured_404()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "2");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));
        TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-10),
            baseTime.AddMinutes(30),
            new[] { baseTime, baseTime.AddMinutes(5) });

        using var client = app.CreateClient();
        var response = await GetTimeSeriesRaw(
            client,
            "Pomona",
            "QLD",
            baseTime.AddHours(2),
            baseTime.AddHours(3));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TIME_RANGE_ERROR", json.RootElement.GetProperty("errorCode").GetString());
        var available = json.RootElement.GetProperty("details").GetProperty("availableRange");
        Assert.True(available.GetProperty("oldestObservationTime").GetString() is not null);
    }

    [Fact]
    public async Task Cache_folders_grouping_is_preserved_and_flattening_sorts_globally()
    {
        await using var app = new BomServiceFactory(settings => settings["CachedDataTypes:Radar:FrameCount"] = "2");
        var now = app.Clock.UtcNow;
        var baseTime = RoundDownToFiveMinutes(now.AddHours(-2));

        TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-30),
            baseTime.AddMinutes(15),
            new[] { baseTime, baseTime.AddMinutes(10) });
        TestCache.SeedSnapshot(
            app.CacheDirectory,
            "Pomona",
            "QLD",
            now.AddMinutes(-20),
            baseTime.AddMinutes(20),
            new[] { baseTime.AddMinutes(5), baseTime.AddMinutes(15) });

        using var client = app.CreateClient();
        var result = await GetTimeSeries(client, "Pomona", "QLD", baseTime, baseTime.AddMinutes(20));

        Assert.Equal(2, result.CacheFolders.Count);
        var flattened = result.CacheFolders
            .SelectMany(folder => folder.Frames.Select(frame => new
            {
                Frame = frame,
                Folder = folder.CacheFolderName
            }))
            .OrderBy(item => item.Frame.AbsoluteObservationTime)
            .ToList();

        Assert.Equal(
            new[]
            {
                baseTime,
                baseTime.AddMinutes(5),
                baseTime.AddMinutes(10),
                baseTime.AddMinutes(15)
            },
            flattened.Select(item => item.Frame.AbsoluteObservationTime!.Value));
    }

    private static async Task<BomLocalService.Models.RadarTimeSeriesResponse> GetTimeSeries(
        HttpClient client,
        string suburb,
        string state,
        DateTime? start,
        DateTime? end)
    {
        var response = await GetTimeSeriesRaw(client, suburb, state, start?.ToString("o"), end?.ToString("o"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadTimeSeries(response);
    }

    private static Task<HttpResponseMessage> GetTimeSeriesRaw(
        HttpClient client,
        string suburb,
        string state,
        DateTime? start,
        DateTime? end) =>
        GetTimeSeriesRaw(client, suburb, state, start?.ToString("o"), end?.ToString("o"));

    private static async Task<HttpResponseMessage> GetTimeSeriesRaw(
        HttpClient client,
        string suburb,
        string state,
        string? start,
        string? end)
    {
        var query = new List<string>();
        if (start != null)
        {
            query.Add("startTime=" + Uri.EscapeDataString(start));
        }

        if (end != null)
        {
            query.Add("endTime=" + Uri.EscapeDataString(end));
        }

        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
        return await client.GetAsync($"/api/radar/{suburb}/{state}/timeseries{suffix}");
    }

    private static async Task<BomLocalService.Models.RadarTimeSeriesResponse> ReadTimeSeries(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<BomLocalService.Models.RadarTimeSeriesResponse>(json, TestJson.Options)!;
    }

    private static DateTime[] FlattenObservations(BomLocalService.Models.RadarTimeSeriesResponse response) =>
        response.CacheFolders
            .SelectMany(folder => folder.Frames)
            .Select(frame => frame.AbsoluteObservationTime!.Value)
            .OrderBy(value => value)
            .ToArray();

    private static DateTime RoundDownToFiveMinutes(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute - value.Minute % 5, 0, DateTimeKind.Utc);
}
