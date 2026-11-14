using BomLocalService.Extensions;
using BomLocalService.Services;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

builder.Services.AddCorsConfiguration(builder.Configuration);

// Register all services via assembly scanning. Services with explicit ownership
// (refresh queue, capture boundary, clock and location state) are registered below
// and deliberately do not implement the automatic-registration markers.
builder.Services.ScanAndRegisterServices(typeof(Program).Assembly);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ILocationStateStore, LocationStateStore>();
builder.Services.AddSingleton<IRadarCaptureService, RadarCaptureService>();

// The queue, worker and hosted service must be the same instance.
builder.Services.AddSingleton<CacheRefreshService>();
builder.Services.AddSingleton<ICacheRefreshQueue>(sp => sp.GetRequiredService<CacheRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CacheRefreshService>());

var app = builder.Build();

var enableHttpsRedirection = builder.Configuration.GetValue<bool>("EnableHttpsRedirection", false);
if (enableHttpsRedirection)
{
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseStaticFiles();
app.UseCors();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=RadarTest}/{action=Index}/{suburb?}/{state?}");

app.MapControllers();

// Liveness remains a process check. It never contacts BOM.
app.MapHealthChecks("/api/health");

// Readiness checks only local prerequisites. An empty cache is ready; an
// upstream outage must not restart the container.
app.MapGet("/api/ready", (ICacheRefreshQueue refreshQueue, ICacheService cacheService) =>
{
    var checks = new Dictionary<string, bool>();
    string? cacheError = null;

    try
    {
        var cacheDirectory = cacheService.GetCacheDirectory();
        var relativeProbe = $".readiness-{Guid.NewGuid():N}.tmp";

        // Never recreate the configured root here. A missing or replaced root
        // must fail closed rather than being repaired by the readiness probe.
        if (!FilePathHelper.TryResolveOwnedPath(
                cacheDirectory,
                relativeProbe,
                allowMissingLeaf: true,
                out var probe,
                out var probeError))
        {
            checks["cacheWritable"] = false;
            cacheError = probeError ?? "UNSAFE_CACHE_ROOT";
        }
        else
        {
            File.WriteAllText(probe!, "ok");

            // Re-resolve after writing. If the root or path changed underneath
            // us, leave the probe alone and report not-ready.
            if (!FilePathHelper.TryResolveOwnedPath(
                    cacheDirectory,
                    relativeProbe,
                    allowMissingLeaf: false,
                    out var cleanupProbe,
                    out var cleanupError))
            {
                checks["cacheWritable"] = false;
                cacheError = cleanupError ?? "CACHE_ROOT_CHANGED";
            }
            else
            {
                File.Delete(cleanupProbe!);
                checks["cacheWritable"] = true;
            }
        }
    }
    catch (Exception ex)
    {
        checks["cacheWritable"] = false;
        cacheError = ex.GetType().Name;
    }

    checks["refreshWorker"] = refreshQueue.IsRunning;
    var ready = checks.Values.All(value => value);

    return Results.Json(new
    {
        status = ready ? "ready" : "not_ready",
        checks,
        queueDepth = refreshQueue.QueueDepth,
        cacheError,
        timestamp = DateTime.UtcNow
    }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

var cacheService = app.Services.GetRequiredService<ICacheService>();
var deletedCount = cacheService.CleanupIncompleteCacheFolders();
if (deletedCount > 0)
{
    app.Logger.LogInformation("Startup recovery: cleaned up {Count} incomplete cache artifact(s)", deletedCount);
}

app.Run();

public partial class Program { }
