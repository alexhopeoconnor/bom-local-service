using BomLocalService.Services.Scraping.Workflows;

namespace BomLocalService.Services.Scraping;

public class WorkflowFactory : IWorkflowFactory
{
    private readonly IServiceProvider _serviceProvider;
    
    public WorkflowFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }
    
    public IWorkflow<TResponse> GetWorkflow<TResponse>(string name)
    {
        return name switch
        {
            "RadarScraping" => _serviceProvider.GetRequiredService<IRadarScrapingWorkflow>() as IWorkflow<TResponse>
                ?? throw new InvalidOperationException($"IRadarScrapingWorkflow is not IWorkflow<{typeof(TResponse).Name}>"),
            "TemperatureMap" => _serviceProvider.GetRequiredService<ITemperatureMapWorkflow>() as IWorkflow<TResponse>
                ?? throw new InvalidOperationException($"ITemperatureMapWorkflow is not IWorkflow<{typeof(TResponse).Name}>"),
            _ => throw new ArgumentException($"Unknown workflow: {name}")
        };
    }
}

