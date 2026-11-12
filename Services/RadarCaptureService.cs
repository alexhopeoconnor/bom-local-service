using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public sealed class RadarCaptureService : IRadarCaptureService
{
    private readonly ILogger<RadarCaptureService> _logger;
    private readonly IBrowserService _browserService;
    private readonly IScrapingService _scrapingService;
    private readonly IDebugService _debugService;

    public RadarCaptureService(
        ILogger<RadarCaptureService> logger,
        IBrowserService browserService,
        IScrapingService scrapingService,
        IDebugService debugService)
    {
        _logger = logger;
        _browserService = browserService;
        _scrapingService = scrapingService;
        _debugService = debugService;
    }

    public async Task<RadarResponse> CaptureAsync(
        LocationId location,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var debugFolder = _debugService.CreateRequestFolder(requestId);
        IBrowserContext? context = null;
        CancellationTokenRegistration cancellationRegistration = default;
        Task? cancellationCloseTask = null;

        try
        {
            context = await _browserService.CreateContextAsync(cancellationToken);

            // Closing the context is what actually stops in-flight Playwright
            // operations. WaitAsync(token) alone would leave Chromium work running.
            cancellationRegistration = cancellationToken.Register(() =>
            {
                cancellationCloseTask = CloseContextSafelyAsync(context, requestId);
            });

            var (page, consoleMessages, networkRequests) =
                await _browserService.CreatePageWithDebugAsync(context, requestId);

            var result = await _scrapingService.ScrapeRadarScreenshotAsync(
                location.Suburb,
                location.State,
                stagingDirectory,
                debugFolder,
                page,
                consoleMessages,
                networkRequests,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Playwright failure during capture for {Location} (RequestId: {RequestId})", location, requestId);
            if (!cancellationToken.IsCancellationRequested)
            {
                await _browserService.ResetAsync(CancellationToken.None);
            }

            throw new RadarCaptureException("BROWSER_ERROR", "Chromium failed during capture", ex);
        }
        finally
        {
            cancellationRegistration.Dispose();
            if (cancellationCloseTask != null)
            {
                await cancellationCloseTask;
            }

            if (context != null)
            {
                await CloseContextSafelyAsync(context, requestId);
            }

            CleanupEmptyDebugFolder(debugFolder);
        }
    }

    private async Task CloseContextSafelyAsync(IBrowserContext? context, string requestId)
    {
        if (context == null)
        {
            return;
        }

        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Context already closed for request {RequestId}", requestId);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error closing context for request {RequestId}", requestId);
        }
    }

    private void CleanupEmptyDebugFolder(string? debugFolder)
    {
        if (string.IsNullOrEmpty(debugFolder) || !Directory.Exists(debugFolder))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(debugFolder).Any())
            {
                Directory.Delete(debugFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove empty debug folder: {DebugFolder}", debugFolder);
        }
    }
}

public sealed class RadarCaptureException : Exception
{
    public RadarCaptureException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
