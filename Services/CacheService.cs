using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

public class CacheService : ICacheService
{
    private static readonly Regex StagingNamePattern = new(
        @"^(?<prefix>.+)_(?<date>\d{8})_(?<time>\d{6})(?:_(?<suffix>[0-9a-f]{8,}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<CacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly double _cacheExpirationMinutes;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<string, (DateTime startTime, CacheUpdatePhase phase, int? currentFrame, int? totalFrames)> _updateProgress = new();
    private readonly ConcurrentQueue<double> _recentTotalDurations = new();
    private readonly ConcurrentDictionary<CacheUpdatePhase, ConcurrentQueue<double>> _phaseDurations = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _stepDurations = new();
    private readonly object _metricsLock = new();
    private const int MaxSamples = 20;

    public CacheService(
        ILogger<CacheService> logger,
        IConfiguration configuration,
        IClock clock)
    {
        _logger = logger;
        _configuration = configuration;
        _clock = clock;
        _cacheDirectory = Path.GetFullPath(FilePathHelper.GetCacheDirectory(configuration));

        _cacheExpirationMinutes = configuration.GetValue<double?>("CacheExpirationMinutes")
            ?? throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");

        if (_cacheExpirationMinutes <= 0)
        {
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0", nameof(configuration));
        }

        Directory.CreateDirectory(_cacheDirectory);
        Directory.CreateDirectory(FilePathHelper.GetStagingRoot(_cacheDirectory));
        Directory.CreateDirectory(FilePathHelper.GetServiceStateRoot(_cacheDirectory));
    }

    public string GetCacheDirectory() => _cacheDirectory;

    public bool IsCacheValid(LastUpdatedInfo? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        return _clock.UtcNow < metadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
    }

    public async Task<CacheSnapshot?> GetLatestSnapshotAsync(
        LocationId location,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await GetSnapshotsAsync(location, dataType, cancellationToken);
        return snapshots.Count == 0 ? null : snapshots[^1];
    }

    public async Task<IReadOnlyList<CacheSnapshot>> GetSnapshotsAsync(
        LocationId location,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CacheSnapshot>();

        foreach (var entry in GetCacheInventory())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.Location.Equals(location) || !entry.IsComplete)
            {
                continue;
            }

            var snapshot = await LoadSnapshotAsync(entry, dataType, cancellationToken);
            if (snapshot != null)
            {
                result.Add(snapshot);
            }
        }

        return result
            .OrderBy(s => s.CacheTimestampUtc)
            .ThenBy(s => s.FolderName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<CacheSnapshot>> GetAllSnapshotsAsync(
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CacheSnapshot>();
        foreach (var entry in GetCacheInventory().Where(e => e.IsComplete))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await LoadSnapshotAsync(entry, dataType, cancellationToken);
            if (snapshot != null)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    public async Task<CacheSnapshot?> GetSnapshotAsync(
        LocationId location,
        string cacheFolderName,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOwnedSnapshot(location, cacheFolderName, out var entry, out var error))
        {
            _logger.LogDebug("Rejected cache folder lookup {FolderName}: {Error}", cacheFolderName, error);
            return null;
        }

        return await LoadSnapshotAsync(entry!, dataType, cancellationToken);
    }

    public async Task<CacheSnapshot?> GetSnapshotContainingFrameAsync(
        LocationId location,
        string cacheFolderName,
        int frameIndex,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(location, cacheFolderName, dataType, cancellationToken);
        return snapshot?.Frames.Any(f => f.FrameIndex == frameIndex) == true ? snapshot : null;
    }

    public IReadOnlyList<CacheInventoryEntry> GetCacheInventory()
    {
        var entries = new List<CacheInventoryEntry>();
        if (!Directory.Exists(_cacheDirectory))
        {
            return entries;
        }

        foreach (var directory in Directory.EnumerateDirectories(_cacheDirectory))
        {
            var folderName = Path.GetFileName(directory);
            if (IsReservedDirectoryName(folderName))
            {
                continue;
            }

            if (FilePathHelper.IsSymlinkOrReparsePoint(directory))
            {
                _logger.LogWarning("Ignoring symbolic-link cache directory: {Folder}", folderName);
                continue;
            }

            if (!LocationHelper.TryParseCacheFolderName(folderName, out var parsed))
            {
                _logger.LogDebug("Ignoring unrecognised cache directory: {Folder}", folderName);
                continue;
            }

            var validation = CacheHelper.ValidateSnapshot(
                directory,
                parsed!.Location,
                CachedDataType.Radar,
                _configuration,
                _clock.UtcNow);

            entries.Add(new CacheInventoryEntry(
                folderName,
                directory,
                parsed.Location,
                parsed.CacheTimestampUtc,
                validation.IsValid));
        }

        return entries
            .OrderBy(e => e.CacheTimestampUtc)
            .ThenBy(e => e.FolderName, StringComparer.Ordinal)
            .ToArray();
    }

    public string CreateStagingDirectory(LocationId location, DateTime capturedAtUtc)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var path = FilePathHelper.GetStagingFolderPath(_cacheDirectory, location, capturedAtUtc, suffix);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(FilePathHelper.GetDataTypeFolderPath(path, CachedDataType.Radar));
        _logger.LogDebug("Created staging directory for {Location}: {Path}", location, path);
        return path;
    }

    public async Task SaveMetadataAsync(
        string cacheFolderPath,
        LastUpdatedInfo metadata,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = FilePathHelper.GetMetadataFilePath(cacheFolderPath);
        var tempPath = metadataPath + ".tmp";
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, metadataPath, overwrite: true);
        _logger.LogDebug("Saved metadata to: {Path}", metadataPath);
    }

    public async Task SaveFramesMetadataAsync(
        string cacheFolderPath,
        CachedDataType dataType,
        List<RadarFrame> frames,
        CancellationToken cancellationToken = default)
    {
        var framesMetadata = frames.Select(frame => new FrameMetadata
        {
            FrameIndex = frame.FrameIndex,
            ObservationTime = frame.AbsoluteObservationTime
                ?? throw new InvalidOperationException($"Frame {frame.FrameIndex} missing AbsoluteObservationTime")
        }).ToList();

        var framesPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
        var framesDirectory = Path.GetDirectoryName(framesPath);
        if (!string.IsNullOrEmpty(framesDirectory))
        {
            Directory.CreateDirectory(framesDirectory);
        }

        var tempPath = framesPath + ".tmp";
        var json = JsonSerializer.Serialize(framesMetadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, framesPath, overwrite: true);
        _logger.LogDebug("Saved frames metadata to: {Path}", framesPath);
    }

    public async Task<CacheSnapshot> PublishSnapshotAsync(
        LocationId location,
        string stagingDirectory,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var stagingRoot = FilePathHelper.GetStagingRoot(_cacheDirectory);
        if (!FilePathHelper.TryResolveDirectChild(
                stagingRoot,
                Path.GetFileName(stagingDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                out var resolvedStaging,
                out var stagingError))
        {
            throw new InvalidCacheFolderException(stagingError ?? "Invalid staging directory");
        }

        var stagingName = resolvedStaging!.FolderName;
        if (!TryParseStagingName(stagingName, out var parsedLocation, out var capturedAtUtc) ||
            !parsedLocation!.Equals(location))
        {
            throw new InvalidCacheFolderException("Staging directory does not belong to the requested location");
        }

        var validation = CacheHelper.ValidateSnapshot(
            resolvedStaging.FullPath,
            location,
            dataType,
            _configuration,
            _clock.UtcNow);
        if (!validation.IsValid)
        {
            throw new InvalidSnapshotException(validation.ErrorCode ?? "SNAPSHOT_INVALID", validation.Error ?? "Snapshot validation failed");
        }

        var markerPath = FilePathHelper.GetSnapshotCompleteMarkerPath(resolvedStaging.FullPath);
        var markerTempPath = markerPath + ".tmp";
        var markerJson = JsonSerializer.Serialize(new
        {
            publishedAtUtc = _clock.UtcNow,
            location = LocationHelper.GetLocationKey(location),
            frameCount = CacheHelper.GetFrameCountForDataType(_configuration, dataType)
        });
        await File.WriteAllTextAsync(markerTempPath, markerJson, cancellationToken);
        File.Move(markerTempPath, markerPath, overwrite: true);

        var finalName = FilePathHelper.GetFinalFolderName(location, capturedAtUtc);
        var finalPath = Path.Combine(_cacheDirectory, finalName);
        var timestamp = capturedAtUtc;

        // Same-location captures are serialized by the refresh queue, but a
        // timestamp collision is still handled deterministically rather than
        // overwriting or merging with an existing snapshot.
        for (var attempt = 0; Directory.Exists(finalPath) && attempt < 120; attempt++)
        {
            timestamp = timestamp.AddSeconds(1);
            finalName = FilePathHelper.GetFinalFolderName(location, timestamp);
            finalPath = Path.Combine(_cacheDirectory, finalName);
        }

        if (Directory.Exists(finalPath))
        {
            throw new InvalidCacheFolderException("Could not allocate a unique snapshot folder name");
        }

        Directory.Move(resolvedStaging.FullPath, finalPath);
        _logger.LogInformation("Published snapshot {Folder} for {Location}", finalName, location);

        if (!LocationHelper.TryParseCacheFolderName(finalName, out var finalParsed))
        {
            throw new InvalidCacheFolderException("Published snapshot name could not be parsed");
        }

        var entry = new CacheInventoryEntry(
            finalName,
            finalPath,
            finalParsed!.Location,
            finalParsed.CacheTimestampUtc,
            true);

        var snapshot = await LoadSnapshotAsync(entry, dataType, cancellationToken);
        if (snapshot == null)
        {
            throw new InvalidSnapshotException("SNAPSHOT_INVALID", "Published snapshot could not be loaded");
        }

        return snapshot;
    }

    public Task<bool> DeleteCachedLocationAsync(
        LocationId location,
        CancellationToken cancellationToken = default)
    {
        var deleted = false;
        foreach (var entry in GetCacheInventory())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Location.Equals(location))
            {
                continue;
            }

            if (TryDeleteFolder(entry.FolderName, out _))
            {
                deleted = true;
            }
        }

        if (deleted)
        {
            _logger.LogInformation("Deleted all cached folders for location: {Location}", location);
        }

        return Task.FromResult(deleted);
    }

    public Task<bool> DeleteSnapshotFolderAsync(
        string folderName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryDeleteFolder(folderName, out _));
    }

    public int CleanupIncompleteCacheFolders()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return 0;
        }

        var deletedCount = 0;

        foreach (var entry in GetCacheInventory())
        {
            if (entry.IsComplete)
            {
                continue;
            }

            if (TryDeleteFolder(entry.FolderName, out _))
            {
                _logger.LogInformation("Removed incomplete cache folder from previous session: {Folder}", entry.FolderName);
                deletedCount++;
            }
        }

        var stagingRoot = FilePathHelper.GetStagingRoot(_cacheDirectory);
        if (Directory.Exists(stagingRoot))
        {
            foreach (var staging in Directory.EnumerateDirectories(stagingRoot))
            {
                var name = Path.GetFileName(staging);
                if (FilePathHelper.IsSymlinkOrReparsePoint(staging))
                {
                    _logger.LogWarning("Ignoring symbolic-link staging directory: {Folder}", name);
                    continue;
                }

                try
                {
                    Directory.Delete(staging, recursive: true);
                    deletedCount++;
                    _logger.LogInformation("Removed abandoned staging folder: {Folder}", name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove abandoned staging folder: {Folder}", name);
                }
            }
        }

        foreach (var unknown in Directory.EnumerateDirectories(_cacheDirectory))
        {
            var name = Path.GetFileName(unknown);
            if (IsReservedDirectoryName(name) || LocationHelper.TryParseCacheFolderName(name, out _))
            {
                continue;
            }

            _logger.LogWarning("Leaving unrecognised cache directory untouched: {Folder}", name);
        }

        return deletedCount;
    }

    private bool TryResolveOwnedSnapshot(
        LocationId location,
        string folderName,
        out CacheInventoryEntry? entry,
        out string? error)
    {
        entry = null;
        error = null;

        if (!LocationHelper.TryParseCacheFolderName(folderName, out var parsed))
        {
            error = "Cache folder name is not a recognised snapshot folder";
            return false;
        }

        if (!parsed!.Location.Equals(location))
        {
            error = "Cache folder belongs to a different location";
            return false;
        }

        if (!FilePathHelper.TryResolveDirectChild(_cacheDirectory, folderName, out var resolved, out error))
        {
            return false;
        }

        var validation = CacheHelper.ValidateSnapshot(
            resolved!.FullPath,
            location,
            CachedDataType.Radar,
            _configuration,
            _clock.UtcNow);
        if (!validation.IsValid)
        {
            error = validation.Error;
            return false;
        }

        entry = new CacheInventoryEntry(
            folderName,
            resolved.FullPath,
            parsed.Location,
            parsed.CacheTimestampUtc,
            true);
        return true;
    }

    private async Task<CacheSnapshot?> LoadSnapshotAsync(
        CacheInventoryEntry entry,
        CachedDataType dataType,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadataPath = FilePathHelper.GetMetadataFilePath(entry.FolderPath);
            var framesPath = FilePathHelper.GetFramesMetadataFilePath(entry.FolderPath, dataType);

            var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<LastUpdatedInfo>(metadataJson);
            if (metadata == null)
            {
                return null;
            }

            var framesJson = await File.ReadAllTextAsync(framesPath, cancellationToken);
            var frameMetadata = JsonSerializer.Deserialize<List<FrameMetadata>>(framesJson);
            if (frameMetadata == null)
            {
                return null;
            }

            var byIndex = frameMetadata
                .GroupBy(f => f.FrameIndex)
                .ToDictionary(g => g.Key, g => g.First().ObservationTime);

            var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, dataType);
            var frames = new List<RadarFrame>(frameCount);
            for (var index = 0; index < frameCount; index++)
            {
                if (!byIndex.TryGetValue(index, out var observation) ||
                    !CacheHelper.IsUsableTimestamp(observation, _clock.UtcNow))
                {
                    return null;
                }

                var framePath = FilePathHelper.GetFrameFilePath(entry.FolderPath, dataType, index);
                frames.Add(new RadarFrame
                {
                    FrameIndex = index,
                    ImagePath = framePath,
                    AbsoluteObservationTime = observation
                });
            }

            return new CacheSnapshot
            {
                FolderName = entry.FolderName,
                FolderPath = entry.FolderPath,
                CacheTimestampUtc = entry.CacheTimestampUtc,
                Metadata = metadata,
                Frames = frames,
                DataType = dataType
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load snapshot {Folder}", entry.FolderName);
            return null;
        }
    }

    private bool TryDeleteFolder(string folderName, out string? error)
    {
        error = null;
        if (!LocationHelper.TryParseCacheFolderName(folderName, out _))
        {
            error = "Unrecognised cache folder";
            return false;
        }

        if (!FilePathHelper.TryResolveDirectChild(_cacheDirectory, folderName, out var resolved, out error))
        {
            return false;
        }

        try
        {
            Directory.Delete(resolved!.FullPath, recursive: true);
            _logger.LogInformation("Deleted cached folder: {Folder}", folderName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Failed to delete cache folder: {Folder}", folderName);
            return false;
        }
    }

    private static bool IsReservedDirectoryName(string name) =>
        name.StartsWith('.') ||
        string.Equals(name, "debug", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseStagingName(
        string stagingName,
        out LocationId? location,
        out DateTime capturedAtUtc)
    {
        location = null;
        capturedAtUtc = default;

        var match = StagingNamePattern.Match(stagingName);
        if (!match.Success)
        {
            return false;
        }

        var prefix = match.Groups["prefix"].Value;
        var timestampText = $"{match.Groups["date"].Value}_{match.Groups["time"].Value}";
        if (!DateTime.TryParseExact(
                timestampText,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out capturedAtUtc))
        {
            return false;
        }

        var parts = prefix.Split('_');
        if (parts.Length < 2)
        {
            return false;
        }

        var state = parts[^1];
        var suburb = string.Join(' ', parts.Take(parts.Length - 1)).Replace('_', ' ').Trim();
        return LocationId.TryCreate(suburb, state, out location, out _);
    }

    public void RecordUpdateProgressByFolder(
        string cacheFolderPath,
        CacheUpdatePhase phase,
        int? currentFrame = null,
        int? totalFrames = null)
    {
        var folderName = Path.GetFileName(cacheFolderPath.TrimEnd(Path.DirectorySeparatorChar));
        string? locationKey = null;

        if (LocationHelper.TryParseCacheFolderName(folderName, out var parsed))
        {
            locationKey = parsed!.Location.Key;
        }
        else if (TryParseStagingName(folderName, out var stagingLocation, out _))
        {
            locationKey = stagingLocation!.Key;
        }

        if (locationKey == null)
        {
            return;
        }

        if (_updateProgress.TryGetValue(locationKey, out var existing) && existing.phase != phase)
        {
            var phaseDuration = (_clock.UtcNow - existing.startTime).TotalSeconds;
            lock (_metricsLock)
            {
                var durations = _phaseDurations.GetOrAdd(existing.phase, _ => new ConcurrentQueue<double>());
                durations.Enqueue(phaseDuration);
                while (durations.Count > MaxSamples)
                {
                    durations.TryDequeue(out _);
                }
            }
        }

        var startTime = _updateProgress.TryGetValue(locationKey, out var current)
            ? current.startTime
            : _clock.UtcNow;
        _updateProgress[locationKey] = (startTime, phase, currentFrame, totalFrames);
    }

    public int GetEstimatedRemainingSeconds(string locationKey)
    {
        if (!_updateProgress.TryGetValue(locationKey, out var progress))
        {
            return 0;
        }

        if ((_clock.UtcNow - progress.startTime).TotalHours > 1)
        {
            _updateProgress.TryRemove(locationKey, out _);
            return 0;
        }

        var elapsed = (_clock.UtcNow - progress.startTime).TotalSeconds;
        var avgTotal = GetAverageTotalDuration();
        if (avgTotal <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(Math.Max(0, avgTotal - elapsed));
    }

    public void RecordStepCompletion(string stepName, double durationSeconds)
    {
        lock (_metricsLock)
        {
            var durations = _stepDurations.GetOrAdd(stepName, _ => new ConcurrentQueue<double>());
            durations.Enqueue(durationSeconds);
            while (durations.Count > MaxSamples)
            {
                durations.TryDequeue(out _);
            }
        }
    }

    public double GetAverageStepDuration(string stepName)
    {
        lock (_metricsLock)
        {
            return _stepDurations.TryGetValue(stepName, out var durations) && durations.Count > 0
                ? durations.Average()
                : 0;
        }
    }

    public Dictionary<string, double> GetStepMetrics()
    {
        lock (_metricsLock)
        {
            return _stepDurations
                .Where(kvp => kvp.Value.Count > 0)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Average());
        }
    }

    public double GetAverageTotalDuration()
    {
        lock (_metricsLock)
        {
            if (_recentTotalDurations.Count == 0)
            {
                return 0;
            }

            var durations = _recentTotalDurations.ToArray();
            Array.Sort(durations);
            return durations.Length % 2 == 0
                ? (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2.0
                : durations[durations.Length / 2];
        }
    }
}

public sealed class InvalidCacheFolderException : Exception
{
    public InvalidCacheFolderException(string message) : base(message)
    {
    }
}

public sealed class InvalidSnapshotException : Exception
{
    public InvalidSnapshotException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
