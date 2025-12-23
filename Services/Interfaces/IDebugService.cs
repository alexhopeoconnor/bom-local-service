using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for debug file generation during web scraping operations.
/// When enabled, saves screenshots, HTML, console logs, and network request information for troubleshooting.
/// </summary>
public interface IDebugService : ISingletonService
{
    /// <summary>
    /// Indicates whether debug mode is enabled.
    /// When false, debug methods return immediately without saving files.
    /// </summary>
    bool IsEnabled { get; }
    
    /// <summary>
    /// Creates a folder for storing debug files for a specific request.
    /// Folder structure: {CacheDirectory}/debug/{requestId}/
    /// </summary>
    /// <param name="requestId">Unique identifier for the request (e.g., timestamp + GUID)</param>
    /// <returns>Full path to the created debug folder, or empty string if debug is disabled</returns>
    string CreateRequestFolder(string requestId);
    
    /// <summary>
    /// Saves debug files for a scraping step.
    /// Creates a step folder (e.g., "step_01_homepage_loaded") containing:
    /// - screenshot.png (full page screenshot)
    /// - page.html (complete HTML source)
    /// - console.log (browser console messages)
    /// - network.log (network request summary)
    /// </summary>
    /// <param name="requestFolder">The debug folder path for this request (from CreateRequestFolder)</param>
    /// <param name="stepNumber">Step number for ordering (e.g., 1, 2, 3)</param>
    /// <param name="stepName">Descriptive name for the step (e.g., "homepage_loaded", "search_button_clicked")</param>
    /// <param name="page">The Playwright page instance to capture</param>
    /// <param name="consoleMessages">Optional list of console messages to save</param>
    /// <param name="networkRequests">Optional list of network requests to save</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveStepDebugAsync(
        string requestFolder, 
        int stepNumber, 
        string stepName, 
        IPage page, 
        List<(string type, string text, DateTime timestamp)>? consoleMessages = null, 
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves debug files when an error occurs during scraping.
    /// Creates an "error" subfolder containing:
    /// - error.txt (error message and stack trace)
    /// - screenshot.png (page state at error time, if page available)
    /// - page.html (HTML source at error time, if page available)
    /// - console.log (console messages up to error)
    /// - network.log (network requests up to error)
    /// </summary>
    /// <param name="requestFolder">The debug folder path for this request (from CreateRequestFolder)</param>
    /// <param name="errorMessage">The error message and/or stack trace to save</param>
    /// <param name="page">Optional Playwright page instance to capture error state</param>
    /// <param name="consoleMessages">Optional list of console messages to save</param>
    /// <param name="networkRequests">Optional list of network requests to save</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveErrorDebugAsync(
        string requestFolder, 
        string errorMessage, 
        IPage? page = null, 
        List<(string type, string text, DateTime timestamp)>? consoleMessages = null, 
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null, 
        CancellationToken cancellationToken = default);
}

