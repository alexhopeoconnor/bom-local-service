using BomLocalService.Models;
using BomLocalService.Services.Browser;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public sealed class RadarCaptureService : IRadarCaptureService
{
    private static readonly TimeSpan BrowserResetWaitBudget = TimeSpan.FromSeconds(15);

    private readonly ILogger<RadarCaptureService> _logger;
    private readonly IBrowserService _browserService;
    private readonly IScrapingService _scrapingService;
    private readonly IDebugService _debugService;
    private readonly ICacheService _cacheService;

    public RadarCaptureService(
        ILogger<RadarCaptureService> logger,
        IBrowserService browserService,
        IScrapingService scrapingService,
        IDebugService debugService,
        ICacheService cacheService)
    {
        _logger = logger;
        _browserService = browserService;
        _scrapingService = scrapingService;
        _debugService = debugService;
        _cacheService = cacheService;
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
        var failed = false;

        try
        {
            var createdContext = await _browserService.CreateContextAsync(cancellationToken);
            context = createdContext;
            cancellationToken.ThrowIfCancellationRequested();

            // Releasing the context is what actually stops in-flight Playwright
            // operations. WaitAsync(token) alone would leave Chromium work running.
            // ReleaseContextAsync is single-flight, so this callback and the finally
            // block below join the same close.
            cancellationRegistration = cancellationToken.Register(
                () => ReleaseContextAfterCancellation(createdContext));

            var (page, consoleMessages, networkRequests) =
                await _browserService.CreatePageWithDebugAsync(createdContext, requestId, cancellationToken);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failed = true;
            throw;
        }
        catch (OperationCanceledException ex)
        {
            failed = true;
            throw new RadarCaptureException(
                "BROWSER_ERROR",
                "Browser operation was cancelled unexpectedly",
                ex);
        }
        catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested)
        {
            failed = true;
            _logger.LogInformation(
                ex,
                "Capture cancelled for {Location} while Playwright was active (RequestId: {RequestId})",
                location,
                requestId);
            throw new OperationCanceledException("Radar capture was cancelled", ex, cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            failed = true;
            _logger.LogWarning(
                ex,
                "Playwright failure during capture for {Location} (RequestId: {RequestId})",
                location,
                requestId);
            await ResetBrowserQuietlyAsync(requestId);
            throw new RadarCaptureException("BROWSER_ERROR", "Chromium failed during capture", ex);
        }
        catch (BrowserOperationTimeoutException ex) when (!cancellationToken.IsCancellationRequested)
        {
            failed = true;
            _logger.LogWarning(
                ex,
                "Browser {Operation} exceeded its {TimeoutSeconds}s budget for {Location} (RequestId: {RequestId})",
                ex.OperationName,
                ex.Timeout.TotalSeconds,
                location,
                requestId);
            await ResetBrowserQuietlyAsync(requestId);
            throw new RadarCaptureException("BROWSER_ERROR", "Chromium setup did not complete in time", ex);
        }
        catch (BrowserSetupException ex) when (!cancellationToken.IsCancellationRequested)
        {
            failed = true;
            _logger.LogWarning(
                ex,
                "Browser setup failed for {Location} (RequestId: {RequestId})",
                location,
                requestId);
            await ResetBrowserQuietlyAsync(requestId);
            throw new RadarCaptureException("BROWSER_ERROR", "Chromium setup failed", ex);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            failed = true;
            throw new OperationCanceledException("Radar capture was cancelled", ex, cancellationToken);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            cancellationRegistration.Dispose();

            var clean = true;
            if (context is not null)
            {
                try
                {
                    clean = await _browserService.ReleaseContextAsync(context, CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    // Never let a cleanup failure replace the capture's original
                    // cancellation or error. The failed close still counts as
                    // unclean so a successful capture reports BROWSER_CLEANUP_ERROR.
                    clean = false;
                    _logger.LogWarning(
                        releaseEx,
                        "Context release failed for request {RequestId}",
                        requestId);
                }
            }

            CleanupEmptyDebugFolder(requestId, debugFolder);

            if (!clean && !failed && !cancellationToken.IsCancellationRequested)
            {
                throw new RadarCaptureException(
                    "BROWSER_CLEANUP_ERROR",
                    "Browser context did not close within the cleanup budget");
            }

            if (!failed)
            {
                // Cleanup can outlive the deadline. Do not report a successful
                // capture when the caller's deadline expired while releasing the
                // context.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private void ReleaseContextAfterCancellation(IBrowserContext context)
    {
        _ = ReleaseContextAfterCancellationAsync(context);
    }

    private async Task ReleaseContextAfterCancellationAsync(IBrowserContext context)
    {
        try
        {
            await _browserService.ReleaseContextAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context release after cancellation did not complete cleanly");
        }
    }

    private async Task ResetBrowserQuietlyAsync(string requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BrowserResetWaitBudget);
            await _browserService.ResetAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Timed out requesting a browser reset for request {RequestId}; the next capture will recreate Chromium if needed",
                requestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request a browser reset for request {RequestId}", requestId);
        }
    }

    private void CleanupEmptyDebugFolder(string requestId, string? debugFolder)
    {
        if (string.IsNullOrEmpty(debugFolder))
        {
            return;
        }

        try
        {
            // The debug directory is owned by the cache root. Resolve the request
            // folder through the same helper that validates cache paths, and only
            // delete it when it is an empty, non-link tree beneath that root.
            var actualCacheRoot = _cacheService.GetCacheDirectory();
            var relativePath = $"{FilePathHelper.DebugDirectoryName}/{requestId}";

            if (!FilePathHelper.TryResolveOwnedPath(
                    actualCacheRoot,
                    relativePath,
                    allowMissingLeaf: false,
                    out var resolvedPath,
                    out var resolveError))
            {
                _logger.LogDebug(
                    "Debug folder could not be resolved for cleanup: {RelativePath} ({Error})",
                    relativePath,
                    resolveError);
                return;
            }

            if (resolvedPath is null ||
                !Directory.Exists(resolvedPath) ||
                Directory.EnumerateFileSystemEntries(resolvedPath).Any())
            {
                return;
            }

            if (!FilePathHelper.TryDeleteOwnedTree(actualCacheRoot, relativePath, out var deleteError))
            {
                _logger.LogDebug(
                    "Refusing to remove debug folder {RelativePath}: {Error}",
                    relativePath,
                    deleteError);
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
