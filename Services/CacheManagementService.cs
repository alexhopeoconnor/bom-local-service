using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Extensions.Hosting;

namespace BomLocalService.Services;

public class CacheManagementService : BackgroundService, IHostedServiceRegistration
{
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IBomRadarService _bomRadarService;
    private readonly ICacheService _cacheService;
    private readonly ILocationStateStore _stateStore;
    private readonly IBrowserService _browserService;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _locationStaggerInterval;
    private readonly TimeSpan _initialDelayInterval;

    public CacheManagementService(
        ILogger<CacheManagementService> logger,
        IBomRadarService bomRadarService,
        ICacheService cacheService,
        ILocationStateStore stateStore,
        IBrowserService browserService,
        IConfiguration configuration)
    {
        _logger = logger;
        _bomRadarService = bomRadarService;
        _cacheService = cacheService;
        _stateStore = stateStore;
        _browserService = browserService;

        var checkIntervalMinutes = configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes")
            ?? throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
        if (checkIntervalMinutes is <= 0 or > 60)
        {
            throw new ArgumentException("CacheManagement:CheckIntervalMinutes must be between 1 and 60", nameof(configuration));
        }

        _checkInterval = TimeSpan.FromMinutes(checkIntervalMinutes);

        var staggerSeconds = configuration.GetValue<int?>("CacheManagement:LocationStaggerSeconds")
            ?? throw new InvalidOperationException("CacheManagement:LocationStaggerSeconds configuration is required.");
        _locationStaggerInterval = TimeSpan.FromSeconds(Math.Max(0, staggerSeconds));

        var initialDelaySeconds = configuration.GetValue<int?>("CacheManagement:InitialDelaySeconds")
            ?? throw new InvalidOperationException("CacheManagement:InitialDelaySeconds configuration is required.");
        _initialDelayInterval = TimeSpan.FromSeconds(Math.Max(0, initialDelaySeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache management service started. Check interval: {Interval}", _checkInterval);

        try
        {
            await Task.Delay(_initialDelayInterval, stoppingToken);
            await _browserService.InitializeAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser pre-warm failed; captures will initialise it on demand");
        }

        await RunCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache management cycle");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var locations = await GetLocationsToManageAsync(cancellationToken);
        _logger.LogInformation("Starting cache check for {Count} known location(s)", locations.Count);

        var queued = 0;
        var valid = 0;
        var rejected = 0;

        foreach (var location in locations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var status = await _bomRadarService.TriggerCacheUpdateAsync(
                    location,
                    RefreshRequestSource.Background,
                    cancellationToken);

                if (status.CacheIsValid)
                {
                    valid++;
                }
                else if (status.UpdateTriggered || status.Message?.Contains("in progress", StringComparison.OrdinalIgnoreCase) == true)
                {
                    queued++;
                }
                else if (!string.IsNullOrEmpty(status.RejectionCode))
                {
                    rejected++;
                    _logger.LogDebug(
                        "Refresh not accepted for {Location}: {Code} (next retry {NextRetry})",
                        location, status.RejectionCode, status.NextRetryAt);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking/updating cache for {Location}", location);
            }

            if (_locationStaggerInterval > TimeSpan.Zero)
            {
                await Task.Delay(_locationStaggerInterval, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Cache check completed: {Valid} valid, {Queued} queued/running, {Rejected} rejected by backoff or queue policy",
            valid, queued, rejected);
    }

    private async Task<List<LocationId>> GetLocationsToManageAsync(CancellationToken cancellationToken)
    {
        var locations = new Dictionary<string, LocationId>(StringComparer.Ordinal);

        foreach (var entry in _cacheService.GetCacheInventory())
        {
            locations[entry.Location.Key] = entry.Location;
        }

        // Failed first-time locations have no complete cache folder yet, so
        // their persisted retry state is the only discovery source.
        foreach (var state in await _stateStore.GetAllAsync(cancellationToken))
        {
            if (LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
            {
                locations[location!.Key] = location;
            }
        }

        return locations.Values
            .OrderBy(location => location.Key, StringComparer.Ordinal)
            .ToList();
    }
}
