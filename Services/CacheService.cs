using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

public class CacheService : ICacheService
{
    private readonly ILogger<CacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly double _cacheExpirationMinutes;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<string, (DateTime startTime, CacheUpdatePhase phase, int? currentFrame, int? totalFrames)> _updateProgress = new();
    private readonly ConcurrentQueue<double> _recentTotalDurations = new();
    private readonly ConcurrentDictionary<CacheUpdatePhase, ConcurrentQueue<double>> _phaseDurations = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _stepDurations = new();
    private readonly ConcurrentDictionary<string, string> _stagingLocationKeys = new(StringComparer.Ordinal);
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

        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                _cacheDirectory,
                FilePathHelper.StagingDirectoryName,
                out _,
                out var stagingError))
        {
            throw new InvalidOperationException($"Staging directory is unsafe: {stagingError}");
        }

        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                _cacheDirectory,
                FilePathHelper.ServiceStateDirectoryName,
                out _,
                out var serviceError))
        {
            throw new InvalidOperationException($"Service state directory is unsafe: {serviceError}");
        }
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
        if (!FilePathHelper.TryValidateCacheRoot(_cacheDirectory, out var cacheRoot, out var rootError))
        {
            _logger.LogWarning("Refusing to enumerate an unsafe cache root: {Error}", rootError);
            return entries;
        }

        if (!Directory.Exists(cacheRoot))
        {
            return entries;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(cacheRoot!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate cache root; returning an empty inventory");
            return entries;
        }

        foreach (var directory in directories)
        {
            var folderName = Path.GetFileName(directory);
            if (FilePathHelper.IsReservedDirectoryName(folderName))
            {
                continue;
            }

            try
            {
                if (!FilePathHelper.TryResolveDirectChild(
                        _cacheDirectory,
                        folderName,
                        out var resolved,
                        out var resolveError))
                {
                    _logger.LogWarning(
                        "Ignoring unsafe or missing cache directory {Folder}: {Error}",
                        folderName,
                        resolveError);
                    continue;
                }

                if (!LocationHelper.TryParseCacheFolderName(folderName, out var parsed))
                {
                    _logger.LogDebug("Ignoring unrecognised cache directory: {Folder}", folderName);
                    continue;
                }

                if (!CacheHelper.TryResolveSnapshotIdentity(
                        _cacheDirectory,
                        parsed!,
                        out var identity,
                        out var identityError) ||
                    identity is null)
                {
                    _logger.LogWarning(
                        "Ignoring cache directory with invalid identity {Folder}: {Error}",
                        folderName,
                        identityError);
                    continue;
                }

                var validation = CacheHelper.ValidateSnapshot(
                    _cacheDirectory,
                    resolved!.FullPath,
                    identity,
                    CachedDataType.Radar,
                    _configuration,
                    _clock.UtcNow);

                entries.Add(new CacheInventoryEntry(
                    folderName,
                    resolved!.FullPath,
                    identity,
                    parsed!.CacheTimestampUtc,
                    validation.IsValid));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ignoring cache directory that could not be inspected: {Folder}", folderName);
            }
        }

        return entries
            .OrderBy(e => e.CacheTimestampUtc)
            .ThenBy(e => e.FolderName, StringComparer.Ordinal)
            .ToArray();
    }

    public string CreateStagingDirectory(LocationId location, DateTime capturedAtUtc)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        if (!LocationHelper.TryGetStagingFolderName(location, capturedAtUtc, suffix, out var stagingName, out var nameError))
        {
            throw new InvalidCacheFolderException(nameError ?? "Could not create a staging folder name");
        }

        var stagingRoot = FilePathHelper.GetStagingRoot(_cacheDirectory);
        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                stagingRoot,
                stagingName!,
                out var staging,
                out var stagingError))
        {
            throw new InvalidCacheFolderException(stagingError ?? "Could not create staging directory");
        }

        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                staging!.FullPath,
                CachedDataType.Radar.ToString().ToLowerInvariant(),
                out _,
                out var dataTypeError))
        {
            throw new InvalidCacheFolderException(dataTypeError ?? "Could not create staging data directory");
        }

        _stagingLocationKeys[staging!.FullPath] = location.Key;
        _logger.LogDebug("Created staging directory for {Location}: {Path}", location, staging!.FullPath);
        return staging!.FullPath;
    }

    public async Task SaveMetadataAsync(
        string cacheFolderPath,
        LastUpdatedInfo metadata,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetOwnedRelativePath(cacheFolderPath, out var relativeFolder, out var relativeError))
        {
            throw new InvalidCacheFolderException(relativeError ?? "Cache folder is not owned by this cache root");
        }

        var relativePath = $"{relativeFolder}/metadata.json";
        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheDirectory,
                relativePath,
                allowMissingLeaf: true,
                out var metadataPath,
                out var pathError))
        {
            throw new InvalidCacheFolderException(pathError ?? "metadata.json path is unsafe");
        }

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await WriteAtomicTextAsync(metadataPath!, json, cancellationToken);
        _logger.LogDebug("Saved metadata to: {Path}", metadataPath);
    }

    public async Task SaveFramesMetadataAsync(
        string cacheFolderPath,
        CachedDataType dataType,
        List<RadarFrame> frames,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetOwnedRelativePath(cacheFolderPath, out var relativeFolder, out var relativeError))
        {
            throw new InvalidCacheFolderException(relativeError ?? "Cache folder is not owned by this cache root");
        }

        var framesMetadata = frames.Select(frame => new FrameMetadata
        {
            FrameIndex = frame.FrameIndex,
            ObservationTime = frame.AbsoluteObservationTime
                ?? throw new InvalidOperationException($"Frame {frame.FrameIndex} missing AbsoluteObservationTime")
        }).ToList();

        var dataTypeFolder = dataType.ToString().ToLowerInvariant();
        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                cacheFolderPath,
                dataTypeFolder,
                out _,
                out var directoryError))
        {
            throw new InvalidCacheFolderException(directoryError ?? "Could not create data type directory");
        }

        var relativePath = $"{relativeFolder}/{dataTypeFolder}/frames.json";
        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheDirectory,
                relativePath,
                allowMissingLeaf: true,
                out var framesPath,
                out var pathError))
        {
            throw new InvalidCacheFolderException(pathError ?? "frames.json path is unsafe");
        }

        var json = JsonSerializer.Serialize(framesMetadata, new JsonSerializerOptions { WriteIndented = true });
        await WriteAtomicTextAsync(framesPath!, json, cancellationToken);
        _logger.LogDebug("Saved frames metadata to: {Path}", framesPath);
    }

    public async Task<CacheSnapshot> PublishSnapshotAsync(
        LocationId location,
        string stagingDirectory,
        CachedDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var stagingRoot = FilePathHelper.GetStagingRoot(_cacheDirectory);
        var stagingName = Path.GetFileName(stagingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (!FilePathHelper.TryResolveDirectChild(
                stagingRoot,
                stagingName,
                out var resolvedStaging,
                out var stagingError))
        {
            throw new InvalidCacheFolderException(stagingError ?? "Invalid staging directory");
        }

        if (!LocationHelper.TryParseCacheFolderName(stagingName, allowStaging: true, out var parsed) ||
            !parsed!.IsStaging ||
            !parsed.Location.Equals(location))
        {
            throw new InvalidCacheFolderException("Staging directory does not belong to the requested location");
        }

        var validation = CacheHelper.ValidateSnapshot(
            stagingRoot,
            resolvedStaging!.FullPath,
            location,
            dataType,
            _configuration,
            _clock.UtcNow);
        if (!validation.IsValid)
        {
            throw new InvalidSnapshotException(validation.ErrorCode ?? "SNAPSHOT_INVALID", validation.Error ?? "Snapshot validation failed");
        }

        var marker = new SnapshotMarker
        {
            FormatVersion = 2,
            PublishedAtUtc = _clock.UtcNow,
            Location = LocationHelper.GetLocationKey(location),
            Suburb = location.Suburb,
            State = location.State,
            FrameCount = CacheHelper.GetFrameCountForDataType(_configuration, dataType)
        };

        var markerRelative = $"{stagingName}/.complete";
        if (!FilePathHelper.TryResolveOwnedPath(
                stagingRoot,
                markerRelative,
                allowMissingLeaf: true,
                out var markerPath,
                out var markerError))
        {
            throw new InvalidCacheFolderException(markerError ?? "Completion marker path is unsafe");
        }

        var markerJson = JsonSerializer.Serialize(marker);
        await WriteAtomicTextAsync(markerPath!, markerJson, cancellationToken);

        var timestamp = parsed!.CacheTimestampUtc;
        var finalName = string.Empty;
        var finalPath = string.Empty;

        // Same-location captures are serialized by the refresh queue, but a
        // timestamp collision is still handled deterministically rather than
        // overwriting or merging with an existing snapshot.
        for (var attempt = 0; ; attempt++)
        {
            if (!FilePathHelper.TryGetFinalFolderName(location, timestamp, out var candidateName, out var nameError))
            {
                throw new InvalidCacheFolderException(nameError ?? "Could not create a snapshot folder name");
            }

            if (!FilePathHelper.TryResolveOwnedPath(
                    _cacheDirectory,
                    candidateName!,
                    allowMissingLeaf: true,
                    out var candidatePath,
                    out var candidateError))
            {
                throw new InvalidCacheFolderException(candidateError ?? "Snapshot destination path is unsafe");
            }

            if (!Directory.Exists(candidatePath) && !File.Exists(candidatePath))
            {
                finalName = candidateName!;
                finalPath = candidatePath!;
                break;
            }

            if (attempt >= 120)
            {
                throw new InvalidCacheFolderException("Could not allocate a unique snapshot folder name");
            }

            timestamp = timestamp.AddSeconds(1);
        }

        Directory.Move(resolvedStaging!.FullPath, finalPath);
        _stagingLocationKeys.TryRemove(resolvedStaging!.FullPath, out _);
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

            if (TryDeleteFolder(entry.FolderName, out var deleteError))
            {
                deleted = true;
            }
            else if (!string.IsNullOrEmpty(deleteError))
            {
                _logger.LogWarning(
                    "Refusing to delete unsafe or unreadable cache folder {Folder}: {Error}",
                    entry.FolderName,
                    deleteError);
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
        if (!FilePathHelper.TryValidateCacheRoot(_cacheDirectory, out var cacheRoot, out var rootError))
        {
            _logger.LogWarning("Refusing startup cache cleanup for an unsafe cache root: {Error}", rootError);
            return 0;
        }

        if (!Directory.Exists(cacheRoot))
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
            if (!FilePathHelper.TryResolveDirectChild(
                    _cacheDirectory,
                    FilePathHelper.StagingDirectoryName,
                    out var safeStagingRoot,
                    out var stagingRootError))
            {
                _logger.LogWarning("Ignoring unsafe staging root: {Error}", stagingRootError);
            }
            else
            {
                string[] stagingDirectories;
                try
                {
                    stagingDirectories = Directory.GetDirectories(safeStagingRoot!.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not enumerate staging root");
                    stagingDirectories = Array.Empty<string>();
                }

                foreach (var staging in stagingDirectories)
                {
                    var name = Path.GetFileName(staging);
                    if (!FilePathHelper.TryDeleteOwnedTree(safeStagingRoot!.FullPath, name, out var stagingDeleteError))
                    {
                        _logger.LogWarning(
                            "Refusing to remove unsafe staging folder {Folder}: {Error}",
                            name,
                            stagingDeleteError);
                        continue;
                    }

                    _stagingLocationKeys.TryRemove(staging, out _);
                    deletedCount++;
                    _logger.LogInformation("Removed abandoned staging folder: {Folder}", name);
                }
            }
        }

        string[] rootDirectories;
        try
        {
            if (!FilePathHelper.TryValidateCacheRoot(_cacheDirectory, out var currentRoot, out var currentRootError))
            {
                _logger.LogWarning("Refusing cache root diagnostics enumeration: {Error}", currentRootError);
                return deletedCount;
            }

            rootDirectories = Directory.GetDirectories(currentRoot!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate cache root for unknown-directory diagnostics");
            rootDirectories = Array.Empty<string>();
        }

        foreach (var unknown in rootDirectories)
        {
            var name = Path.GetFileName(unknown);
            if (FilePathHelper.IsReservedDirectoryName(name) || LocationHelper.TryParseCacheFolderName(name, out _))
            {
                continue;
            }

            _logger.LogWarning("Leaving unrecognised cache directory untouched: {Folder}", name);
        }

        return deletedCount;
    }

    private bool TryGetOwnedRelativePath(
        string cacheFolderPath,
        out string relativeFolder,
        out string? error)
    {
        relativeFolder = string.Empty;
        error = null;

        try
        {
            var fullPath = Path.GetFullPath(cacheFolderPath.TrimEnd(Path.DirectorySeparatorChar));
            if (!FilePathHelper.IsUnderRoot(_cacheDirectory, fullPath))
            {
                error = "Cache folder is outside the configured cache root";
                return false;
            }

            var relative = Path.GetRelativePath(_cacheDirectory, fullPath);
            if (relative == ".")
            {
                error = "Cache folder must not be the cache root";
                return false;
            }

            relativeFolder = relative.Replace(Path.DirectorySeparatorChar, '/');
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Cache folder path is not valid";
            return false;
        }
    }

    private async Task WriteAtomicTextAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnedRelativePath(destinationPath, out var relativeDestination, out var relativeError))
        {
            throw new InvalidCacheFolderException(relativeError ?? "Write destination is not owned by this cache root");
        }

        var tempRelativePath = relativeDestination + ".tmp";
        string? destinationError = null;
        string? tempError = null;
        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheDirectory,
                relativeDestination,
                allowMissingLeaf: true,
                out var safeDestination,
                out destinationError) ||
            !FilePathHelper.TryResolveOwnedPath(
                _cacheDirectory,
                tempRelativePath,
                allowMissingLeaf: true,
                out var tempPath,
                out tempError))
        {
            throw new InvalidCacheFolderException(destinationError ?? tempError ?? "Write path is unsafe");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await using (var stream = new FileStream(
                             tempPath!,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            // Revalidate from the cache root immediately before the commit. This
            // narrows, but cannot eliminate, filesystem TOCTOU.
            if (!FilePathHelper.TryResolveOwnedPath(
                    _cacheDirectory,
                    relativeDestination,
                    allowMissingLeaf: true,
                    out safeDestination,
                    out destinationError) ||
                !FilePathHelper.TryResolveOwnedPath(
                    _cacheDirectory,
                    tempRelativePath,
                    allowMissingLeaf: true,
                    out tempPath,
                    out tempError))
            {
                throw new InvalidCacheFolderException(destinationError ?? tempError ?? "Write destination changed while writing");
            }

            File.Move(tempPath!, safeDestination!, overwrite: true);
        }
        finally
        {
            try
            {
                if (FilePathHelper.TryResolveOwnedPath(
                        _cacheDirectory,
                        tempRelativePath,
                        allowMissingLeaf: true,
                        out var safeTempPath,
                        out _) &&
                    File.Exists(safeTempPath) &&
                    !FilePathHelper.IsSymlinkOrReparsePoint(safeTempPath))
                {
                    File.Delete(safeTempPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; never follow a replaced ancestor.
            }
        }
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

        if (!FilePathHelper.TryResolveDirectChild(_cacheDirectory, folderName, out var resolved, out error))
        {
            return false;
        }

        if (!CacheHelper.TryResolveSnapshotIdentity(
                _cacheDirectory,
                parsed!,
                out var identity,
                out var identityError) ||
            identity is null)
        {
            error = identityError ?? "Cache folder identity is invalid";
            return false;
        }

        if (!identity.Equals(location))
        {
            error = "Cache folder belongs to a different location";
            return false;
        }

        var validation = CacheHelper.ValidateSnapshot(
            _cacheDirectory,
            resolved!.FullPath,
            identity,
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
            resolved!.FullPath,
            identity,
            parsed!.CacheTimestampUtc,
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
            var dataTypeFolder = dataType.ToString().ToLowerInvariant();
            var metadataRelative = $"{entry.FolderName}/metadata.json";
            var framesRelative = $"{entry.FolderName}/{dataTypeFolder}/frames.json";

            if (!FilePathHelper.TryResolveOwnedPath(
                    _cacheDirectory,
                    metadataRelative,
                    allowMissingLeaf: false,
                    out var metadataPath,
                    out _) ||
                !CacheHelper.TryReadBoundedJson<LastUpdatedInfo>(
                    metadataPath!,
                    CacheHelper.MaxMetadataBytes,
                    out var metadata,
                    out _) ||
                metadata is null)
            {
                return null;
            }

            if (!FilePathHelper.TryResolveOwnedPath(
                    _cacheDirectory,
                    framesRelative,
                    allowMissingLeaf: false,
                    out var framesPath,
                    out _) ||
                !CacheHelper.TryReadBoundedJson<List<FrameMetadata?>>(
                    framesPath!,
                    CacheHelper.MaxFramesMetadataBytes,
                    out var frameMetadata,
                    out _) ||
                frameMetadata is null ||
                frameMetadata.Any(frame => frame is null))
            {
                return null;
            }

            var byIndex = frameMetadata
                .GroupBy(frame => frame!.FrameIndex)
                .ToDictionary(group => group.Key, group => group.First()!.ObservationTime);

            var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, dataType);
            var frames = new List<RadarFrame>(frameCount);
            for (var index = 0; index < frameCount; index++)
            {
                if (!byIndex.TryGetValue(index, out var observation) ||
                    !CacheHelper.IsUsableTimestamp(observation, _clock.UtcNow))
                {
                    return null;
                }

                var frameRelative = $"{entry.FolderName}/{dataTypeFolder}/frame_{index}.png";
                if (!FilePathHelper.TryResolveOwnedPath(
                        _cacheDirectory,
                        frameRelative,
                        allowMissingLeaf: false,
                        out var framePath,
                        out _))
                {
                    return null;
                }

                frames.Add(new RadarFrame
                {
                    FrameIndex = index,
                    ImagePath = framePath!,
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

        if (!FilePathHelper.TryDeleteOwnedTree(_cacheDirectory, folderName, out error))
        {
            _logger.LogWarning("Refusing to delete unsafe cache folder {Folder}: {Error}", folderName, error);
            return false;
        }

        _logger.LogInformation("Deleted cached folder: {Folder}", folderName);
        return true;
    }

    public void RecordUpdateProgressByFolder(
        string cacheFolderPath,
        CacheUpdatePhase phase,
        int? currentFrame = null,
        int? totalFrames = null)
    {
        var fullPath = Path.GetFullPath(cacheFolderPath.TrimEnd(Path.DirectorySeparatorChar));
        string? locationKey = null;

        if (_stagingLocationKeys.TryGetValue(fullPath, out var stagingKey))
        {
            locationKey = stagingKey;
        }
        else
        {
            var folderName = Path.GetFileName(fullPath);
            if (LocationHelper.TryParseCacheFolderName(folderName, allowStaging: true, out var parsed))
            {
                locationKey = parsed!.Location.Key;
            }
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
