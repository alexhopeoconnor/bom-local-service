using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Interfaces.Registration;
using BomLocalService.Utilities;
using Microsoft.Extensions.Hosting;

namespace BomLocalService.Services;

public class CacheManagementService : BackgroundService, IHostedServiceRegistration
{
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IBomRadarService _bomRadarService;
    private readonly IBrowserService _browserService;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _locationStaggerInterval;
    private readonly TimeSpan _initialDelayInterval;
    private readonly HashSet<string> _activeUpdates = new();
    private readonly object _lock = new();

    public CacheManagementService(
        ILogger<CacheManagementService> logger,
        IBomRadarService bomRadarService,
        IBrowserService browserService,
        IConfiguration configuration)
    {
        _logger = logger;
        _bomRadarService = bomRadarService;
        _browserService = browserService;
        _configuration = configuration;
        
        var checkIntervalMinutesConfig = configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes");
        if (!checkIntervalMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
        }
        var checkIntervalMinutes = checkIntervalMinutesConfig.Value;
        
        if (checkIntervalMinutes <= 0 || checkIntervalMinutes > 60)
        {
            throw new ArgumentException("CacheManagement:CheckIntervalMinutes must be between 1 and 60", nameof(configuration));
        }
        _checkInterval = TimeSpan.FromMinutes(checkIntervalMinutes);
        
        // LocationStaggerSeconds is used for delays between processing locations (both initial and periodic)
        var locationStaggerSecondsConfig = configuration.GetValue<int?>("CacheManagement:LocationStaggerSeconds");
        if (!locationStaggerSecondsConfig.HasValue)
        {
            throw new InvalidOperationException("CacheManagement:LocationStaggerSeconds configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__LOCATIONSTAGGERSECONDS environment variable.");
        }
        _locationStaggerInterval = TimeSpan.FromSeconds(locationStaggerSecondsConfig.Value);
        
        var initialDelaySecondsConfig = configuration.GetValue<int?>("CacheManagement:InitialDelaySeconds");
        if (!initialDelaySecondsConfig.HasValue)
        {
            throw new InvalidOperationException("CacheManagement:InitialDelaySeconds configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__INITIALDELAYSECONDS environment variable.");
        }
        _initialDelayInterval = TimeSpan.FromSeconds(initialDelaySecondsConfig.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache management service started. Check interval: {Interval}", _checkInterval);

        // Wait a bit for the service to fully initialize
        await Task.Delay(_initialDelayInterval, stoppingToken);

        // Pre-warm the browser before starting cache updates
        _logger.LogInformation("Pre-warming browser before cache updates");
        try
        {
            await _browserService.InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-warm browser, will continue anyway");
        }

        // Get initial list of locations to manage (could be from config or discovered from cache)
        var locationsToManage = GetLocationsToManage();

        // Initial cache update on startup
        _logger.LogInformation("Performing initial cache update for {Count} locations", locationsToManage.Count);
        var (initialUpdatesTriggered, initialCachesValid) = await ProcessLocationsAsync(locationsToManage, stoppingToken);
        
        _logger.LogInformation("Initial cache update completed: {UpdatesTriggered} updates triggered, {CachesValid} caches valid", 
            initialUpdatesTriggered, initialCachesValid);

        // Periodic cache updates
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);

                // Refresh list of locations (in case new ones were cached)
                locationsToManage = GetLocationsToManage();
                _logger.LogInformation("Starting periodic cache check for {Count} locations", locationsToManage.Count);

                var (updatesTriggered, cachesValid) = await ProcessLocationsAsync(locationsToManage, stoppingToken);

                _logger.LogInformation("Periodic cache check completed: {UpdatesTriggered} updates triggered, {CachesValid} caches valid", 
                    updatesTriggered, cachesValid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache management cycle");
            }
        }
    }

    /// <summary>
    /// Processes a list of locations, checking and updating cache as needed
    /// </summary>
    private async Task<(int updatesTriggered, int cachesValid)> ProcessLocationsAsync(
        List<(string suburb, string state)> locations, 
        CancellationToken cancellationToken)
    {
        int updatesTriggered = 0;
        int cachesValid = 0;

        foreach (var (suburb, state) in locations)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var (triggered, isValid) = await UpdateCacheIfNeededAsync(suburb, state, cancellationToken);
            if (triggered)
            {
                updatesTriggered++;
            }
            else if (isValid)
            {
                cachesValid++;
            }

            // Stagger processing to avoid overwhelming the system
            await Task.Delay(_locationStaggerInterval, cancellationToken);
        }

        return (updatesTriggered, cachesValid);
    }

    private async Task<(bool updateTriggered, bool isValid)> UpdateCacheIfNeededAsync(string suburb, string state, CancellationToken cancellationToken)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        
        // Check if update is already in progress
        lock (_lock)
        {
            if (_activeUpdates.Contains(locationKey))
            {
                _logger.LogInformation("Update already in progress for {Suburb}, {State}, skipping", suburb, state);
                return (false, false);
            }
            _activeUpdates.Add(locationKey);
        }

        try
        {
            var status = await _bomRadarService.TriggerCacheUpdateAsync(suburb, state, cancellationToken);
            
            if (status.UpdateTriggered)
            {
                _logger.LogInformation("Cache update triggered for {Suburb}, {State} (expired at {ExpiresAt})", 
                    suburb, state, status.CacheExpiresAt);
                return (true, false);
            }
            else if (status.CacheIsValid)
            {
                _logger.LogInformation("Cache is valid for {Suburb}, {State} (expires at {ExpiresAt})", 
                    suburb, state, status.CacheExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "unknown");
                return (false, true);
            }
            else
            {
                _logger.LogInformation("Cache exists but is invalid for {Suburb}, {State}", suburb, state);
                return (false, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking/updating cache for {Suburb}, {State}", suburb, state);
            return (false, false);
        }
        finally
        {
            lock (_lock)
            {
                _activeUpdates.Remove(locationKey);
            }
        }
    }

    private List<(string suburb, string state)> GetLocationsToManage()
    {
        var locations = new List<(string, string)>();
        
        try
        {
            // Get locations from existing cache folders
            var cacheDirectory = FilePathHelper.GetCacheDirectory(_configuration);
            if (Directory.Exists(cacheDirectory))
            {
                // Get all folders (not files)
                var folders = Directory.GetDirectories(cacheDirectory);
                var seen = new HashSet<string>();
                
                foreach (var folder in folders)
                {
                    var folderName = Path.GetFileName(folder);
                    var location = LocationHelper.ParseLocationFromFilename(folderName);
                    if (location.HasValue)
                    {
                        var locationKey = LocationHelper.GetLocationKey(location.Value.suburb, location.Value.state);
                        if (!seen.Contains(locationKey))
                        {
                            seen.Add(locationKey);
                            locations.Add(location.Value);
                        }
                    }
                }
            }

            // Could also read from config if you want to pre-configure locations
            // var configuredLocations = _configuration.GetSection("ManagedLocations").Get<List<LocationConfig>>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error discovering locations from cache");
        }

        return locations;
    }
}

