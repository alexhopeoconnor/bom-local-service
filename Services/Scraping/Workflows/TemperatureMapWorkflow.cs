using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;

namespace BomLocalService.Services.Scraping.Workflows;

/// <summary>
/// Future workflow for temperature map scraping (not yet implemented)
/// </summary>
public class TemperatureMapWorkflow : ITemperatureMapWorkflow
{
    private readonly ILogger<TemperatureMapWorkflow> _logger;
    
    public string Name => "TemperatureMap";
    public string Description => "Scrapes temperature forecast map (future feature)";
    
    public string[] StepNames => new[]
    {
        "NavigateHomepage",
        "ClickSearchButton",
        "FillSearchInput",
        "WaitForSearchResults",
        "SelectSearchResult",
        // Future: "ClickTemperatureMapLink",
        // Future: "WaitForMapReady",
        // Future: "ExtractMetadata",
        // Future: "CalculateMapBounds",
        // Future: "CaptureTemperatureFrames"
    };
    
    public TemperatureMapWorkflow(ILogger<TemperatureMapWorkflow> logger)
    {
        _logger = logger;
    }
    
    public Task<RadarResponse> ExecuteAsync(ScrapingContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException("TemperatureMap workflow is not yet implemented");
    }
}

