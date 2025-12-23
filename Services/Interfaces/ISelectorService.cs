using BomLocalService.Models;
using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service for finding page elements using configured selectors
/// </summary>
public interface ISelectorService : ISingletonService
{
    /// <summary>
    /// Finds an element using the configured selectors, trying each in order until one is found
    /// </summary>
    Task<ILocator?> FindElementAsync(IPage page, SelectorConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a locator for the first selector (assumes element exists)
    /// </summary>
    ILocator GetLocator(IPage page, SelectorConfig config);
}

