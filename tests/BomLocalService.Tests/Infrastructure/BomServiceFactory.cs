using BomLocalService.Services;
using BomLocalService.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BomLocalService.Tests.Infrastructure;

public sealed class BomServiceFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public BomServiceFactory(Action<Dictionary<string, string?>>? configure = null)
    {
        CacheDirectory = Path.Combine(Path.GetTempPath(), "bom-local-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(CacheDirectory);
        Clock = new TestClock(DateTime.UtcNow);
        Capture = new ControllableRadarCaptureService(Clock);

        _settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CacheDirectory"] = CacheDirectory,
            ["CacheExpirationMinutes"] = "12.5",
            ["CacheRetentionHours"] = "24",
            ["CacheManagement:CheckIntervalMinutes"] = "60",
            ["CacheManagement:InitialDelaySeconds"] = "3600",
            ["CacheManagement:LocationStaggerSeconds"] = "0",
            ["CacheCleanup:IntervalHours"] = "24",
            ["Debug:Enabled"] = "false",
            ["TimeSeries:WarningFolderCount"] = "200",
            ["TimeSeries:MaxTimeRangeHours"] = "48",
            ["Refresh:QueueCapacity"] = "8",
            ["Refresh:MaxConcurrentCaptures"] = "1",
            ["Refresh:CaptureTimeoutSeconds"] = "30",
            ["Refresh:BackoffBaseSeconds"] = "60",
            ["Refresh:MaxBackoffSeconds"] = "900",
            ["CachedDataTypes:Radar:FrameCount"] = "7"
        };

        configure?.Invoke(_settings);
    }

    public string CacheDirectory { get; }
    public TestClock Clock { get; }
    public ControllableRadarCaptureService Capture { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(_settings);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(_ => Clock);

            services.RemoveAll<IRadarCaptureService>();
            services.AddSingleton<IRadarCaptureService>(Capture);

            // Deterministic tests own retention and periodic management directly.
            var hostedToRemove = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                                     descriptor.ImplementationType is { } type &&
                                     (type == typeof(CacheManagementService) || type == typeof(CacheCleanupService)))
                .ToList();
            foreach (var descriptor in hostedToRemove)
            {
                services.Remove(descriptor);
            }
        });
    }

    public CacheCleanupService CreateCleanupService() => new(
        Services.GetRequiredService<ILogger<CacheCleanupService>>(),
        Services.GetRequiredService<ICacheService>(),
        Services.GetRequiredService<ICacheRefreshQueue>(),
        Services.GetRequiredService<IClock>(),
        Services.GetRequiredService<IConfiguration>());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, recursive: true);
                }
            }
            catch
            {
                // Temporary test data is best-effort.
            }
        }
    }
}
