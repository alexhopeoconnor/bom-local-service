using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Scraping.Workflows;

/// <summary>
/// Concrete interface for temperature map workflow (hides generic IWorkflow&lt;RadarResponse&gt;)
/// </summary>
public interface ITemperatureMapWorkflow : IWorkflow<RadarResponse>, IScopedService
{
}

