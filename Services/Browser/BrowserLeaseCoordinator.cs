using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace BomLocalService.Services.Browser;

/// <summary>
/// Owns browser/context relationships and bounds cleanup. Contexts are tied to
/// the browser instance that created them. Retiring a browser never closes
/// contexts that are still leased; the retired instance is closed after its last
/// context releases. This keeps a reset caused by one failed capture from
/// aborting a healthy concurrent capture.
/// </summary>
public sealed class BrowserLeaseCoordinator<TBrowser, TContext>
    where TBrowser : class
    where TContext : class
{
    private readonly object _gate = new();
    private readonly Dictionary<TContext, TBrowser> _owners =
        new(ReferenceComparer<TContext>.Instance);
    private readonly Dictionary<TBrowser, int> _contextCounts =
        new(ReferenceComparer<TBrowser>.Instance);
    private readonly HashSet<TBrowser> _retired =
        new(ReferenceComparer<TBrowser>.Instance);
    private readonly ConcurrentDictionary<TContext, Task<bool>> _releases =
        new(ReferenceComparer<TContext>.Instance);
    private readonly TimeSpan _cleanupTimeout;
    private readonly Func<TContext, Task> _closeContext;
    private readonly Func<TBrowser, Task> _closeBrowser;
    private readonly Action<string, Exception?> _observe;
    private TBrowser? _current;

    public BrowserLeaseCoordinator(
        TimeSpan cleanupTimeout,
        Func<TContext, Task> closeContext,
        Func<TBrowser, Task> closeBrowser,
        Action<string, Exception?> observe)
    {
        if (cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        }

        _cleanupTimeout = cleanupTimeout;
        _closeContext = closeContext ?? throw new ArgumentNullException(nameof(closeContext));
        _closeBrowser = closeBrowser ?? throw new ArgumentNullException(nameof(closeBrowser));
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
    }

    /// <summary>The browser used for new contexts, if one is alive.</summary>
    public TBrowser? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public int ActiveContextCount
    {
        get
        {
            lock (_gate)
            {
                return _contextCounts.Values.Sum();
            }
        }
    }

    public int RetiredBrowserCount
    {
        get
        {
            lock (_gate)
            {
                return _retired.Count;
            }
        }
    }

    /// <summary>Installs a newly launched browser as the current instance.</summary>
    public void SetCurrent(TBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("A current browser is already registered.");
            }

            _current = browser;
            _contextCounts.TryAdd(browser, 0);
        }
    }

    /// <summary>
    /// Transfers ownership of a context to the coordinator. The browser must be
    /// the current instance or an already retired instance that is still leased.
    /// </summary>
    public bool Register(TContext context, TBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(browser);
        lock (_gate)
        {
            if (_owners.ContainsKey(context))
            {
                return false;
            }

            if (!ReferenceEquals(_current, browser) && !_retired.Contains(browser))
            {
                return false;
            }

            _owners.Add(context, browser);
            _contextCounts.TryGetValue(browser, out var count);
            _contextCounts[browser] = count + 1;
            return true;
        }
    }

    public bool TryGetBrowser(TContext context, out TBrowser? browser)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            return _owners.TryGetValue(context, out browser);
        }
    }

    /// <summary>
    /// Closes the context within the cleanup budget and releases its lease. The
    /// same context can be released concurrently (for example from a deadline
    /// callback and a finally block); the close is single-flight.
    /// </summary>
    /// <returns>
    /// True when the context closed cleanly. False means the cleanup budget
    /// expired or the close failed; the owning browser has been retired so the
    /// stuck work cannot be silently abandoned.
    /// </returns>
    public Task<bool> ReleaseContextAsync(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = _releases.GetOrAdd(context, ReleaseContextCoreAsync);
        return AwaitReleaseAsync(context, operation);
    }

    /// <summary>
    /// Retires the current browser. When contexts are still leased, Chromium is
    /// left alive until the last context releases; otherwise it is closed now.
    /// </summary>
    public Task RetireCurrentAsync(string reason)
    {
        TBrowser? closeNow = null;
        var active = 0;
        lock (_gate)
        {
            if (_current is null)
            {
                return Task.CompletedTask;
            }

            closeNow = RetireLocked(_current, out active);
            _current = null;
        }

        _observe(
            active == 0
                ? $"Closing retired browser instance ({reason})"
                : $"Retiring browser instance ({reason}); {active} active context(s) keep it alive",
            null);

        return closeNow is null ? Task.CompletedTask : CloseBrowserBoundedAsync(closeNow, reason);
    }

    /// <summary>
    /// Retires a specific browser if it is still current. Used when context
    /// creation fails before a new context could be registered.
    /// </summary>
    public Task RetireBrowserAsync(TBrowser browser, string reason)
    {
        ArgumentNullException.ThrowIfNull(browser);
        TBrowser? closeNow = null;
        var active = 0;
        lock (_gate)
        {
            if (!ReferenceEquals(_current, browser))
            {
                return Task.CompletedTask;
            }

            closeNow = RetireLocked(browser, out active);
            _current = null;
        }

        _observe(
            active == 0
                ? $"Closing retired browser instance ({reason})"
                : $"Retiring browser instance ({reason}); {active} active context(s) keep it alive",
            null);

        return closeNow is null ? Task.CompletedTask : CloseBrowserBoundedAsync(closeNow, reason);
    }

    /// <summary>
    /// Closes a resource that was never registered, such as a context that
    /// completed after its setup budget expired. The close itself is bounded.
    /// </summary>
    public Task CloseLateContextAsync(TContext context, string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CloseContextBoundedAsync(context, reason);
    }

    /// <summary>Closes a browser that was never installed as current.</summary>
    public Task CloseLateBrowserAsync(TBrowser browser, string reason)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return CloseBrowserBoundedAsync(browser, reason);
    }

    /// <summary>
    /// Force-closes the current and every retired browser. Used only for service
    /// disposal/shutdown after hosted work has been asked to stop.
    /// </summary>
    public async Task ReleaseAllAsync(string reason)
    {
        var browsers = new List<TBrowser>();
        lock (_gate)
        {
            if (_current is not null)
            {
                browsers.Add(_current);
                _current = null;
            }

            browsers.AddRange(_retired);
            _retired.Clear();
            _owners.Clear();
            _contextCounts.Clear();
        }

        foreach (var browser in browsers)
        {
            await CloseBrowserBoundedAsync(browser, reason).ConfigureAwait(false);
        }
    }

    private TBrowser? RetireLocked(TBrowser browser, out int active)
    {
        _contextCounts.TryGetValue(browser, out active);
        if (active == 0)
        {
            _contextCounts.Remove(browser);
            return browser;
        }

        _retired.Add(browser);
        return null;
    }

    private async Task<bool> AwaitReleaseAsync(TContext context, Task<bool> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            if (_releases.TryGetValue(context, out var registered) &&
                ReferenceEquals(registered, operation))
            {
                _releases.TryRemove(context, out _);
            }
        }
    }

    private async Task<bool> ReleaseContextCoreAsync(TContext context)
    {
        bool owned;
        lock (_gate)
        {
            owned = _owners.ContainsKey(context);
        }

        if (!owned)
        {
            // Already released (or disposed). Do not close a context this
            // coordinator no longer owns.
            return true;
        }

        var clean = await CloseContextBoundedAsync(context, "releasing capture context").ConfigureAwait(false);

        TBrowser? closeBrowser = null;
        lock (_gate)
        {
            if (_owners.Remove(context, out var browser))
            {
                var remaining = DecrementContextCount(browser);
                if (!clean && ReferenceEquals(_current, browser))
                {
                    _current = null;
                    if (remaining == 0)
                    {
                        _contextCounts.Remove(browser);
                        closeBrowser = browser;
                    }
                    else
                    {
                        _retired.Add(browser);
                    }
                }
                else if (_retired.Contains(browser) && remaining == 0)
                {
                    _retired.Remove(browser);
                    _contextCounts.Remove(browser);
                    closeBrowser = browser;
                }
            }
        }

        if (closeBrowser is not null)
        {
            await CloseBrowserBoundedAsync(closeBrowser, "last context released").ConfigureAwait(false);
        }

        return clean;
    }

    private int DecrementContextCount(TBrowser browser)
    {
        if (!_contextCounts.TryGetValue(browser, out var count))
        {
            return 0;
        }

        var remaining = Math.Max(0, count - 1);
        _contextCounts[browser] = remaining;
        return remaining;
    }

    private async Task<bool> CloseContextBoundedAsync(TContext context, string reason)
    {
        try
        {
            await BoundedBrowserOperation.RunAsync(
                _closeContext(context),
                _cleanupTimeout,
                CancellationToken.None,
                _observe,
                $"close browser context ({reason})").ConfigureAwait(false);
            return true;
        }
        catch (BrowserOperationTimeoutException ex)
        {
            _observe($"Browser context close exceeded the {_cleanupTimeout.TotalSeconds:F0}s cleanup budget ({reason})", ex);
            return false;
        }
        catch (Exception ex)
        {
            _observe($"Browser context close failed ({reason})", ex);
            return false;
        }
    }

    private async Task CloseBrowserBoundedAsync(TBrowser browser, string reason)
    {
        try
        {
            await BoundedBrowserOperation.RunAsync(
                _closeBrowser(browser),
                _cleanupTimeout,
                CancellationToken.None,
                _observe,
                $"close browser ({reason})").ConfigureAwait(false);
        }
        catch (BrowserOperationTimeoutException ex)
        {
            _observe($"Browser close exceeded the {_cleanupTimeout.TotalSeconds:F0}s cleanup budget ({reason})", ex);
        }
        catch (Exception ex)
        {
            _observe($"Browser close failed ({reason})", ex);
        }
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
