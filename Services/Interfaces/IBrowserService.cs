using BomLocalService.Services.Interfaces.Registration;
using Microsoft.Playwright;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Manages the Playwright browser lifecycle and context creation.
/// </summary>
public interface IBrowserService : ISingletonService, IDisposable, IAsyncDisposable
{
    bool IsBrowserConnected { get; }

    /// <summary>
    /// Creates a context and transfers its lease to the caller. The caller must
    /// release it through <see cref="ReleaseContextAsync"/>.
    /// </summary>
    Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a leased context within the cleanup budget and releases ownership.
    /// Safe to call concurrently for the same context; the close is single-flight.
    /// </summary>
    /// <returns>
    /// False when the cleanup budget expired or the close failed. In that case the
    /// owning browser has been retired so the stuck Chromium work is not silently
    /// abandoned. A context whose lease is no longer registered returns true.
    /// </returns>
    Task<bool> ReleaseContextAsync(IBrowserContext context, CancellationToken cancellationToken = default);

    Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context,
        string requestId,
        CancellationToken cancellationToken = default);

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retire the current Chromium instance so the next capture recreates it.
    /// Contexts that are still leased keep the retired instance alive until they
    /// release; they are never aborted by the reset.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
