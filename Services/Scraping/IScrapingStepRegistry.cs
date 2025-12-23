using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Scraping;

/// <summary>
/// Registry for managing scraping steps
/// </summary>
public interface IScrapingStepRegistry : ISingletonService
{
    void RegisterStep(IScrapingStep step);
    IScrapingStep? GetStep(string name);
    IEnumerable<IScrapingStep> GetAllSteps();
}

