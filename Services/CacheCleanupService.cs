using BomLocalService.Services.Interfaces.Registration;
using BomLocalService.Utilities;
using Microsoft.Extensions.Hosting;

namespace BomLocalService.Services;

public class CacheCleanupService : BackgroundService, IHostedServiceRegistration
{
    private readonly ILogger<CacheCleanupService> _logger;
    private readonly string _cacheDirectory;
    private readonly int _retentionHours;
    private readonly TimeSpan _cleanupInterval;

    public CacheCleanupService(ILogger<CacheCleanupService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        
        var retentionHoursConfig = configuration.GetValue<int?>("CacheRetentionHours");
        if (!retentionHoursConfig.HasValue)
        {
            throw new InvalidOperationException("CacheRetentionHours configuration is required. Set it in appsettings.json or via CACHERETENTIONHOURS environment variable.");
        }
        _retentionHours = retentionHoursConfig.Value;
        
        var cleanupIntervalHoursConfig = configuration.GetValue<int?>("CacheCleanup:IntervalHours");
        if (!cleanupIntervalHoursConfig.HasValue)
        {
            throw new InvalidOperationException("CacheCleanup:IntervalHours configuration is required. Set it in appsettings.json or via CACHECLEANUP__INTERVALHOURS environment variable.");
        }
        var cleanupIntervalHours = cleanupIntervalHoursConfig.Value;
        _cleanupInterval = TimeSpan.FromHours(cleanupIntervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache cleanup service started. Retention: {RetentionHours} hours, Interval: {Interval}", 
            _retentionHours, _cleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldCacheFilesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private Task CleanupOldCacheFilesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return Task.CompletedTask;
        }

        var cutoffTime = DateTime.UtcNow.AddHours(-_retentionHours);
        var deletedCount = 0;
        var totalSize = 0L;

        try
        {
            // Get all cache folders
            var folders = Directory.GetDirectories(_cacheDirectory);
            
            foreach (var folder in folders)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var folderInfo = new DirectoryInfo(folder);
                    var folderTime = folderInfo.LastWriteTimeUtc;

                    // Delete if folder is older than retention period
                    if (folderTime < cutoffTime)
                    {
                        var folderSize = GetFolderSize(folder);
                        Directory.Delete(folder, recursive: true);
                        deletedCount++;
                        totalSize += folderSize;

                        _logger.LogDebug("Deleted old cache folder: {Folder} (age: {Age})", 
                            Path.GetFileName(folder), DateTime.UtcNow - folderTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cache folder: {Folder}", folder);
                }
            }

            if (deletedCount > 0)
            {
                var sizeInMB = totalSize / (1024.0 * 1024.0);
                _logger.LogInformation("Cache cleanup completed. Deleted {Count} folders ({Size:F2} MB)", 
                    deletedCount, sizeInMB);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache folder enumeration");
        }

        // Also clean up old debug directories
        try
        {
            var debugDir = Path.Combine(_cacheDirectory, "debug");
            if (Directory.Exists(debugDir))
            {
                var debugDirs = Directory.GetDirectories(debugDir);
                foreach (var dir in debugDirs)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            Directory.Delete(dir, recursive: true);
                            _logger.LogDebug("Deleted old debug directory: {Dir}", dir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete debug directory: {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up debug directories: {Error}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Calculates the total size of a folder and all its contents
    /// </summary>
    private long GetFolderSize(string folderPath)
    {
        long size = 0;
        try
        {
            var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                size += fileInfo.Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating folder size for: {Folder}", folderPath);
        }
        return size;
    }
}

