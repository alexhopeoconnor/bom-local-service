using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Scraping.Workflows;

/// <summary>
/// Concrete interface for radar scraping workflow (hides generic IWorkflow&lt;RadarResponse&gt;)
/// </summary>
public interface IRadarScrapingWorkflow : IWorkflow<RadarResponse>, IScopedService
{
}

