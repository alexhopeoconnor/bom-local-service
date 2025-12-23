using BomLocalService.Extensions;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
// AddControllersWithViews includes AddControllers, so we use it for both MVC and API controllers
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

// Configure CORS - MUST be added before other services
builder.Services.AddCorsConfiguration(builder.Configuration);

// Register all services via assembly scanning
builder.Services.ScanAndRegisterServices(typeof(Program).Assembly);

var app = builder.Build();

// Configure the HTTP request pipeline
// HTTPS redirection is optional and disabled by default for Docker flexibility
// Users can enable it by setting EnableHttpsRedirection=true in appsettings.json or ENABLEHTTPSREDIRECTION=true environment variable
var enableHttpsRedirection = builder.Configuration.GetValue<bool>("EnableHttpsRedirection", false);
if (enableHttpsRedirection)
{
    app.UseHttpsRedirection();
}

// Enable routing (required for CORS to work with controllers)
app.UseRouting();

// CORS middleware - MUST be after UseRouting but before MapControllers
app.UseCors();

// No authorization required - service is designed to run behind a reverse proxy if auth is needed

// Map MVC routes first (before API routes to avoid conflicts)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=RadarTest}/{action=Index}/{suburb?}/{state?}");

// Map API controllers (with /api prefix)
app.MapControllers();

// Map health check endpoint for Docker health monitoring
app.MapHealthChecks("/api/health");

// Cleanup incomplete cache folders from previous crashes/restarts before starting services
var cacheService = app.Services.GetRequiredService<ICacheService>();
var deletedCount = cacheService.CleanupIncompleteCacheFolders();
if (deletedCount > 0)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Startup recovery: cleaned up {Count} incomplete cache folder(s) from previous session", deletedCount);
}

// Cleanup on shutdown
app.Lifetime.ApplicationStopped.Register(() =>
{
    var service = app.Services.GetService<IBomRadarService>();
    if (service is IDisposable disposable)
    {
        disposable.Dispose();
    }
});

app.Run();
