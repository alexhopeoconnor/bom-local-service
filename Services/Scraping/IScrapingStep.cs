using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Scraping;

/// <summary>
/// Interface for a single scraping step
/// Steps are stateless (all request data is in ScrapingContext), so singleton lifetime is appropriate
/// </summary>
public interface IScrapingStep : ISingletonService
{
    /// <summary>
    /// Unique name of the step
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Names of steps that must complete before this step can execute
    /// </summary>
    string[] Prerequisites { get; }
    
    /// <summary>
    /// Checks if the step can execute in the current page state
    /// </summary>
    bool CanExecute(ScrapingContext context);
    
    /// <summary>
    /// Executes the step
    /// </summary>
    Task<ScrapingStepResult> ExecuteAsync(ScrapingContext context, CancellationToken cancellationToken);
}

