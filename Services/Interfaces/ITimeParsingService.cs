using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for parsing time and metadata information from BOM website content.
/// Extracts observation times, forecast times, weather station names, and distances from BOM weather map pages.
/// </summary>
public interface ITimeParsingService : ISingletonService
{
    /// <summary>
    /// Extracts last updated information from a BOM weather map page.
    /// Scrapes the weather metadata section to get observation time, forecast time, weather station, and distance.
    /// </summary>
    /// <param name="page">The Playwright page instance containing the loaded BOM weather map</param>
    /// <returns>Last updated information with parsed times and metadata</returns>
    Task<LastUpdatedInfo> ExtractLastUpdatedInfoAsync(IPage page);
    
    /// <summary>
    /// Parses last updated text from BOM website to extract structured information.
    /// Handles various text formats like "Observations: 11 minutes ago, 8:20 pm AEST at Gympie weather station, 30 km from Pomona, QLD".
    /// Converts times to UTC using the configured timezone.
    /// </summary>
    /// <param name="text">The raw text content from the BOM weather metadata section</param>
    /// <returns>Parsed last updated information with observation time, forecast time, weather station, and distance</returns>
    LastUpdatedInfo ParseLastUpdatedText(string text);
}

