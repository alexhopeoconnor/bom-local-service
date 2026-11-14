using System.Security.Cryptography;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Utilities;

namespace BomLocalService.Tests.Infrastructure;

public static class TestCache
{
    public static string SeedSnapshot(
        string cacheDirectory,
        string suburb,
        string state,
        DateTime cacheTimestampUtc,
        DateTime metadataObservationUtc,
        IReadOnlyList<DateTime> frameObservations,
        string? folderName = null,
        bool completeMarker = false,
        string weatherStation = "Test Station",
        string distance = "10 km")
    {
        var location = LocationId.Create(suburb, state);
        if (folderName is null)
        {
            if (!LocationHelper.TryGetCacheFolderBaseName(location, cacheTimestampUtc, out var generatedName, out var nameError))
            {
                throw new ArgumentException(nameError, nameof(suburb));
            }

            folderName = generatedName!;
        }

        var folderPath = Path.Combine(cacheDirectory, folderName!);
        var radarPath = Path.Combine(folderPath, "radar");
        Directory.CreateDirectory(radarPath);

        for (var index = 0; index < frameObservations.Count; index++)
        {
            File.WriteAllBytes(
                Path.Combine(radarPath, $"frame_{index}.png"),
                TestPng.Create((byte)(20 + index * 17), (byte)(80 + index * 9), (byte)(140 - index * 7)));
        }

        var metadata = new LastUpdatedInfo
        {
            ObservationTime = DateTime.SpecifyKind(metadataObservationUtc, DateTimeKind.Utc),
            ForecastTime = DateTime.SpecifyKind(metadataObservationUtc.AddHours(-1), DateTimeKind.Utc),
            WeatherStation = weatherStation,
            Distance = distance
        };
        File.WriteAllText(
            Path.Combine(folderPath, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        var frameMetadata = frameObservations
            .Select((observation, index) => new FrameMetadata
            {
                FrameIndex = index,
                ObservationTime = DateTime.SpecifyKind(observation, DateTimeKind.Utc)
            })
            .ToList();
        File.WriteAllText(
            Path.Combine(radarPath, "frames.json"),
            JsonSerializer.Serialize(frameMetadata, new JsonSerializerOptions { WriteIndented = true }));

        if (completeMarker)
        {
            File.WriteAllText(Path.Combine(folderPath, ".complete"), "{}");
        }

        return folderPath;
    }

    public static RadarResponse WriteStagingSnapshot(
        string stagingDirectory,
        LocationId location,
        DateTime capturedAtUtc,
        DateTime metadataObservationUtc,
        int frameCount = 7,
        TimeSpan? spacing = null)
    {
        spacing ??= TimeSpan.FromMinutes(5);
        var observations = Enumerable.Range(0, frameCount)
            .Select(index => metadataObservationUtc.AddMinutes(-5) - TimeSpan.FromMinutes((frameCount - 1 - index) * spacing.Value.TotalMinutes))
            .ToList();

        var radarPath = Path.Combine(stagingDirectory, "radar");
        Directory.CreateDirectory(radarPath);

        var frames = new List<RadarFrame>();
        for (var index = 0; index < frameCount; index++)
        {
            var path = Path.Combine(radarPath, $"frame_{index}.png");
            File.WriteAllBytes(path, TestPng.Create((byte)(30 + index * 13), (byte)(100 + index * 5), (byte)(200 - index * 11)));
            frames.Add(new RadarFrame
            {
                FrameIndex = index,
                ImagePath = path,
                AbsoluteObservationTime = observations[index]
            });
        }

        var metadata = new LastUpdatedInfo
        {
            ObservationTime = metadataObservationUtc,
            ForecastTime = metadataObservationUtc.AddHours(-1),
            WeatherStation = "Test Station",
            Distance = "10 km"
        };

        File.WriteAllText(
            Path.Combine(stagingDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(radarPath, "frames.json"),
            JsonSerializer.Serialize(frames.Select(frame => new FrameMetadata
            {
                FrameIndex = frame.FrameIndex,
                ObservationTime = frame.AbsoluteObservationTime!.Value
            }), new JsonSerializerOptions { WriteIndented = true }));

        return new RadarResponse
        {
            Frames = frames,
            ObservationTime = metadataObservationUtc,
            ForecastTime = metadata.ForecastTime,
            WeatherStation = metadata.WeatherStation,
            Distance = metadata.Distance,
            LastUpdated = capturedAtUtc,
            CacheIsValid = true
        };
    }

    public static Dictionary<string, string> SnapshotHashes(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file);
            result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return result;
    }

    public static string CreateSentinel(string directory, string name = "outside-sentinel.txt", string content = "untouched")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
