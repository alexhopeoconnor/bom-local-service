using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;
using BomLocalService.Utilities;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Safe cache storage: snapshot validation, publication, lookup and cleanup.
/// User-derived folder names are never used as search patterns.
/// </summary>
public interface ICacheService : ISingletonService
{
    string GetCacheDirectory();

    bool IsCacheValid(LastUpdatedInfo? metadata);

    /// <summary>Deletes recognized incomplete captures and stale staging folders.
    /// Unknown directories are left untouched and logged.</summary>
    int CleanupIncompleteCacheFolders();

    Task<CacheSnapshot?> GetLatestSnapshotAsync(
        LocationId location,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CacheSnapshot>> GetSnapshotsAsync(
        LocationId location,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    Task<CacheSnapshot?> GetSnapshotAsync(
        LocationId location,
        string cacheFolderName,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    Task<CacheSnapshot?> GetSnapshotContainingFrameAsync(
        LocationId location,
        string cacheFolderName,
        int frameIndex,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    /// <summary>All complete snapshots for retention and diagnostics.</summary>
    Task<IReadOnlyList<CacheSnapshot>> GetAllSnapshotsAsync(
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    IReadOnlyList<CacheInventoryEntry> GetCacheInventory();

    string CreateStagingDirectory(LocationId location, DateTime capturedAtUtc);

    Task SaveMetadataAsync(string cacheFolderPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default);

    Task SaveFramesMetadataAsync(string cacheFolderPath, CachedDataType dataType, List<RadarFrame> frames, CancellationToken cancellationToken = default);

    Task<CacheSnapshot> PublishSnapshotAsync(
        LocationId location,
        string stagingDirectory,
        CachedDataType dataType,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteCachedLocationAsync(
        LocationId location,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSnapshotFolderAsync(
        string folderName,
        CancellationToken cancellationToken = default);

    void RecordUpdateProgressByFolder(string cacheFolderPath, CacheUpdatePhase phase, int? currentFrame = null, int? totalFrames = null);
    int GetEstimatedRemainingSeconds(string locationKey);
    void RecordStepCompletion(string stepName, double durationSeconds);
    double GetAverageStepDuration(string stepName);
    Dictionary<string, double> GetStepMetrics();
    double GetAverageTotalDuration();
}

public sealed record CacheInventoryEntry(
    string FolderName,
    string FolderPath,
    LocationId Location,
    DateTime CacheTimestampUtc,
    bool IsComplete);
