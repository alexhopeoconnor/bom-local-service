using System.Reflection;

namespace BomLocalService.Services.Scraping;

public class ScrapingStepRegistry : IScrapingStepRegistry
{
    private readonly Dictionary<string, IScrapingStep> _steps = new();
    private readonly ILogger<ScrapingStepRegistry> _logger;
    
    public ScrapingStepRegistry(ILogger<ScrapingStepRegistry> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        AutoRegisterSteps(serviceProvider);
    }
    
    private void AutoRegisterSteps(IServiceProvider serviceProvider)
    {
        var stepTypes = typeof(IScrapingStep).Assembly.GetTypes()
            .Where(t => typeof(IScrapingStep).IsAssignableFrom(t) 
                     && !t.IsInterface 
                     && !t.IsAbstract 
                     && !t.IsGenericType);
        
        foreach (var stepType in stepTypes)
        {
            try
            {
                // Use DI to resolve the step (ensures proper dependency injection)
                var step = (IScrapingStep)serviceProvider.GetRequiredService(stepType);
                RegisterStep(step);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-register step {StepType}", stepType.Name);
            }
        }
    }
    
    public void RegisterStep(IScrapingStep step)
    {
        if (_steps.ContainsKey(step.Name))
        {
            _logger.LogWarning("Step {Name} is already registered, overwriting", step.Name);
        }
        _steps[step.Name] = step;
        _logger.LogDebug("Registered step: {Name}", step.Name);
    }
    
    public IScrapingStep? GetStep(string name)
    {
        return _steps.TryGetValue(name, out var step) ? step : null;
    }
    
    public IEnumerable<IScrapingStep> GetAllSteps()
    {
        return _steps.Values;
    }
}

