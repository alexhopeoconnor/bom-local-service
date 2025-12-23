using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for scraping radar screenshots from the BOM website.
/// Orchestrates the multi-step process of navigating BOM, finding locations, and capturing radar screenshots.
/// </summary>
public interface IScrapingService : ISingletonService
{
    /// <summary>
    /// Scrapes a radar screenshot from the BOM website for a given location.
    /// Performs the complete workflow: navigates to BOM, searches for location, navigates to radar page,
    /// waits for map to render, captures screenshot, extracts metadata, and saves to cache.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cacheFolderPath">The cache folder path where frames and metadata will be saved</param>
    /// <param name="debugFolder">Folder path for saving debug files (screenshots, HTML, logs) if debug mode is enabled</param>
    /// <param name="page">The Playwright page instance to use for scraping</param>
    /// <param name="consoleMessages">List to capture console messages from the browser (for debugging)</param>
    /// <param name="networkRequests">List to capture network request information (for debugging)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Radar screenshot response with image path and metadata</returns>
    Task<RadarResponse> ScrapeRadarScreenshotAsync(
        string suburb,
        string state,
        string cacheFolderPath,
        string debugFolder,
        IPage page,
        List<(string type, string text, DateTime timestamp)> consoleMessages,
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests,
        CancellationToken cancellationToken = default);
}

