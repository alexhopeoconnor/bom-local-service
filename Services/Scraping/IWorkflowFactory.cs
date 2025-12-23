using BomLocalService.Services.Interfaces.Registration;

namespace BomLocalService.Services.Scraping;

/// <summary>
/// Factory for creating workflows
/// </summary>
public interface IWorkflowFactory : ISingletonService
{
    IWorkflow<TResponse> GetWorkflow<TResponse>(string name);
}

