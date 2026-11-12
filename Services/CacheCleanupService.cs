using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Interfaces.Registration;
using BomLocalService.Utilities;
using Microsoft.Extensions.Hosting;

namespace BomLocalService.Services;

/// <summary>
/// Retention cleanup. Unknown directories are never deleted. The newest complete
/// snapshot per location is preserved even when it is older than the retention
/// window, so a stale fallback remains available during upstream outages.
/// </summary>
public class CacheCleanupService : BackgroundService, IHostedServiceRegistration
{
    private readonly ILogger<CacheCleanupService> _logger;
    private readonly ICacheService _cacheService;
    private readonly ICacheRefreshQueue _refreshQueue;
    private readonly IClock _clock;
    private readonly int _retentionHours;
    private readonly TimeSpan _cleanupInterval;

    public CacheCleanupService(
        ILogger<CacheCleanupService> logger,
        ICacheService cacheService,
        ICacheRefreshQueue refreshQueue,
        IClock clock,
        IConfiguration configuration)
    {
        _logger = logger;
        _cacheService = cacheService;
        _refreshQueue = refreshQueue;
        _clock = clock;

        _retentionHours = configuration.GetValue<int?>("CacheRetentionHours")
            ?? throw new InvalidOperationException("CacheRetentionHours configuration is required. Set it in appsettings.json or via CACHERETENTIONHOURS environment variable.");

        var cleanupIntervalHours = configuration.GetValue<int?>("CacheCleanup:IntervalHours")
            ?? throw new InvalidOperationException("CacheCleanup:IntervalHours configuration is required. Set it in appsettings.json or via CACHECLEANUP__INTERVALHOURS environment variable.");

        _cleanupInterval = TimeSpan.FromHours(Math.Max(1, cleanupIntervalHours));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cache cleanup service started. Retention: {RetentionHours} hours, Interval: {Interval}",
            _retentionHours, _cleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
            }

            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one retention pass. Exposed for deterministic tests.
    /// </summary>
    public async Task RunCleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_cacheService.GetCacheDirectory()))
        {
            return;
        }

        var cutoff = _clock.UtcNow.AddHours(-_retentionHours);
        var inventory = _cacheService.GetCacheInventory();
        var latestCompleteByLocation = inventory
            .Where(entry => entry.IsComplete)
            .GroupBy(entry => entry.Location.Key)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.CacheTimestampUtc)
                    .ThenBy(entry => entry.FolderName, StringComparer.Ordinal)
                    .Last());

        var deletedCount = 0;
        var totalSize = 0L;

        foreach (var entry in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_refreshQueue.IsLocationActive(entry.Location))
            {
                _logger.LogDebug("Skipping active capture location during retention: {Location}", entry.Location);
                continue;
            }

            if (!entry.IsComplete)
            {
                if (await _cacheService.DeleteSnapshotFolderAsync(entry.FolderName, cancellationToken))
                {
                    deletedCount++;
                    _logger.LogInformation("Removed incomplete cache folder: {Folder}", entry.FolderName);
                }

                continue;
            }

            if (entry.CacheTimestampUtc >= cutoff)
            {
                continue;
            }

            if (latestCompleteByLocation.TryGetValue(entry.Location.Key, out var latest) &&
                string.Equals(latest.FolderName, entry.FolderName, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Preserving latest complete fallback snapshot beyond retention for {Location}: {Folder}",
                    entry.Location, entry.FolderName);
                continue;
            }

            var size = GetFolderSize(entry.FolderPath);
            if (await _cacheService.DeleteSnapshotFolderAsync(entry.FolderName, cancellationToken))
            {
                deletedCount++;
                totalSize += size;
                _logger.LogDebug(
                    "Deleted expired cache folder: {Folder} (captured {CapturedAt:u})",
                    entry.FolderName, entry.CacheTimestampUtc);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Cache cleanup completed. Deleted {Count} folder(s) ({Size:F2} MB)",
                deletedCount, totalSize / (1024.0 * 1024.0));
        }

        await CleanupDebugFoldersAsync(cutoff, cancellationToken);
    }

    private Task CleanupDebugFoldersAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var debugDir = Path.Combine(_cacheService.GetCacheDirectory(), "debug");
        if (!Directory.Exists(debugDir))
        {
            return Task.CompletedTask;
        }

        foreach (var directory in Directory.EnumerateDirectories(debugDir))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                    _logger.LogDebug("Deleted old debug directory: {Directory}", Path.GetFileName(directory));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete debug directory: {Directory}", directory);
            }
        }

        return Task.CompletedTask;
    }

    private long GetFolderSize(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file).Length)
                .Sum();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not calculate size for {Folder}", folderPath);
            return 0;
        }
    }
}
