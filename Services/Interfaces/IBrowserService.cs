using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for managing Playwright browser instances and automation.
/// Handles browser lifecycle, context creation, and anti-detection measures for web scraping.
/// </summary>
public interface IBrowserService : ISingletonService, IDisposable
{
    /// <summary>
    /// Creates a new browser context with proper configuration for BOM website scraping.
    /// Configures viewport, user agent, timezone, and HTTP headers to mimic a real browser.
    /// </summary>
    /// <returns>A configured browser context ready for page creation</returns>
    Task<IBrowserContext> CreateContextAsync();
    
    /// <summary>
    /// Creates a new page with anti-detection scripts and optional debug event handlers.
    /// Injects comprehensive anti-detection JavaScript to avoid bot detection.
    /// If debug mode is enabled, captures console messages and network requests for debugging.
    /// </summary>
    /// <param name="context">The browser context to create the page in</param>
    /// <param name="requestId">Unique request identifier for debug file organization</param>
    /// <returns>Tuple containing the page, list of console messages, and list of network requests</returns>
    Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context, 
        string requestId);
    
    /// <summary>
    /// Initializes and pre-warms the browser instance.
    /// Launches the Chromium browser to reduce latency on first use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the semaphore used to synchronize browser operations.
    /// Ensures only one browser operation runs at a time to prevent resource conflicts.
    /// </summary>
    /// <returns>The semaphore for synchronizing browser access</returns>
    SemaphoreSlim GetSemaphore();
}

