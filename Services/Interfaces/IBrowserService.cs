using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Manages the Playwright browser lifecycle and context creation.
/// </summary>
public interface IBrowserService : ISingletonService, IDisposable, IAsyncDisposable
{
    bool IsBrowserConnected { get; }

    Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default);

    Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context,
        string requestId);

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Discard a broken browser so the next capture recreates Chromium.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
