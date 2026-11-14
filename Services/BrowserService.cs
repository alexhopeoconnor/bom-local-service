using BomLocalService.Services.Browser;
using BomLocalService.Services.Interfaces;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public sealed class BrowserService : IBrowserService
{
    private const int MinBudgetSeconds = 1;
    private const int MaxBudgetSeconds = 300;

    private readonly ILogger<BrowserService> _logger;
    private readonly string _timezone;
    private readonly IDebugService _debugService;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly TimeSpan _launchTimeout;
    private readonly TimeSpan _setupTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly BrowserLeaseCoordinator<IBrowser, IBrowserContext> _leases;
    private IPlaywright? _playwright;
    private bool _disposed;

    public BrowserService(ILogger<BrowserService> logger, IConfiguration configuration, IDebugService debugService)
    {
        _logger = logger;
        _debugService = debugService;

        var timezone = configuration.GetValue<string>("Timezone");
        if (string.IsNullOrEmpty(timezone))
        {
            throw new InvalidOperationException("Timezone configuration is required. Set it in appsettings.json or via TIMEZONE environment variable.");
        }

        _timezone = timezone;

        _launchTimeout = ReadBudget(configuration, "Browser:LaunchTimeoutSeconds", 60);
        _setupTimeout = ReadBudget(configuration, "Browser:SetupTimeoutSeconds", 30);
        _operationTimeout = ReadBudget(configuration, "Browser:OperationTimeoutSeconds", 30);
        _cleanupTimeout = ReadBudget(configuration, "Browser:CleanupTimeoutSeconds", 10);
        _leases = new BrowserLeaseCoordinator<IBrowser, IBrowserContext>(
            _cleanupTimeout,
            context => context.CloseAsync(),
            CloseBrowserAsync,
            LogObserved);
    }

    public bool IsBrowserConnected
    {
        get
        {
            try
            {
                var browser = _leases.Current;
                return !_disposed && browser?.IsConnected == true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await GetOrCreateBrowserLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            Exception? firstFailure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var browser = await GetOrCreateBrowserLockedAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var context = await BoundedBrowserOperation.RunAsync(
                        browser.NewContextAsync(CreateContextOptions()),
                        _setupTimeout,
                        cancellationToken,
                        reclaim: created => _leases.CloseLateContextAsync(created, "cancelled context setup"),
                        observe: LogObserved,
                        operationName: "create-browser-context",
                        reclaimTimeout: _cleanupTimeout).ConfigureAwait(false);

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ConfigureContext(context);
                        if (!_leases.Register(context, browser))
                        {
                            throw new BrowserSetupException("Browser context was created for a browser that is no longer current.");
                        }

                        return context;
                    }
                    catch
                    {
                        await _leases.CloseLateContextAsync(context, "context adoption failed").ConfigureAwait(false);
                        throw;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The caller's deadline/shutdown token won the race. The helper
                    // has already reclaimed a late context; do not retry.
                    throw;
                }
                catch (Exception ex) when (IsRecoverableContextFailure(ex))
                {
                    // Do not spend the cleanup budget or retry setup once the
                    // caller's deadline has already expired.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (attempt > 0)
                    {
                        await _leases.RetireBrowserAsync(browser, "context creation failed twice").ConfigureAwait(false);
                        throw new BrowserSetupException(
                            "Could not create a browser context after recreating Chromium.",
                            ex);
                    }

                    firstFailure = ex;
                    _logger.LogWarning(ex, "Failed to create browser context; retiring Chromium and retrying once");
                    await _leases.RetireBrowserAsync(browser, "context creation failed").ConfigureAwait(false);
                }
            }

            throw new BrowserSetupException(
                "Could not create a browser context after recreating Chromium.",
                firstFailure);
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async Task<bool> ReleaseContextAsync(
        IBrowserContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Cleanup must not be skipped because the capture deadline fired. The
        // close itself is bounded by the coordinator's cleanup budget.
        _ = cancellationToken;
        return await _leases.ReleaseContextAsync(context).ConfigureAwait(false);
    }

    private BrowserNewContextOptions CreateContextOptions() => new()
    {
        ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
        DeviceScaleFactor = 2.0f,
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        Locale = "en-AU",
        TimezoneId = _timezone,
        Permissions = new[] { "geolocation" },
        ExtraHTTPHeaders = new Dictionary<string, string>
        {
            { "Accept-Language", "en-AU,en;q=0.9" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8" },
            { "Accept-Encoding", "gzip, deflate, br" },
            { "Connection", "keep-alive" },
            { "Upgrade-Insecure-Requests", "1" },
            { "Sec-Fetch-Dest", "document" },
            { "Sec-Fetch-Mode", "navigate" },
            { "Sec-Fetch-Site", "none" },
            { "Cache-Control", "max-age=0" }
        }
    };

    private void ConfigureContext(IBrowserContext context)
    {
        var timeoutMilliseconds = (float)_operationTimeout.TotalMilliseconds;
        context.SetDefaultTimeout(timeoutMilliseconds);
        context.SetDefaultNavigationTimeout(timeoutMilliseconds);
    }

    public async Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var page = await BoundedBrowserOperation.RunAsync(
            context.NewPageAsync(),
            _setupTimeout,
            cancellationToken,
            reclaim: created => ClosePageBoundedAsync(created, requestId),
            observe: LogObserved,
            operationName: "create-page",
            reclaimTimeout: _cleanupTimeout).ConfigureAwait(false);

        try
        {
            var consoleMessages = new List<(string type, string text, DateTime timestamp)>();
            var networkRequests = new List<(string method, string url, int? status, string resourceType, DateTime timestamp)>();

            if (_debugService.IsEnabled)
            {
                page.Console += (_, e) => consoleMessages.Add((e.Type.ToString(), e.Text, DateTime.UtcNow));
                page.PageError += (_, e) => consoleMessages.Add(("error", $"PageError: {e}", DateTime.UtcNow));
                context.Console += (_, e) => consoleMessages.Add((e.Type.ToString(), $"[Context] {e.Text}", DateTime.UtcNow));
                page.Request += (_, e) => networkRequests.Add((e.Method, e.Url, null, e.ResourceType, DateTime.UtcNow));
                page.RequestFailed += (_, e) =>
                {
                    networkRequests.Add((e.Method, e.Url, null, e.ResourceType, DateTime.UtcNow));
                    consoleMessages.Add(("error", $"Request failed: {e.Method} {e.Url} - {e.Failure}", DateTime.UtcNow));
                };
                page.Response += (_, e) =>
                {
                    var matching = networkRequests
                        .Select((request, index) => (request, index))
                        .Where(x => x.request.url == e.Request.Url && x.request.status == null)
                        .ToList();
                    if (matching.Count > 0)
                    {
                        var (request, index) = matching[^1];
                        networkRequests[index] = (request.method, request.url, e.Status, request.resourceType, request.timestamp);
                    }
                };
            }

            await BoundedBrowserOperation.RunAsync(
                page.AddInitScriptAsync(GetAntiDetectionScript()),
                _setupTimeout,
                cancellationToken,
                LogObserved,
                "install-page-init-script").ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return (page, consoleMessages, networkRequests);
        }
        catch
        {
            await ClosePageBoundedAsync(page, requestId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Pre-warming browser");
        try
        {
            await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Browser initialized and ready");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pre-warm browser, will initialize on first use");
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogInformation("Resetting Chromium browser");
            await _leases.RetireCurrentAsync("reset requested").ConfigureAwait(false);
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task<IBrowser> GetOrCreateBrowserLockedAsync(CancellationToken cancellationToken)
    {
        var current = _leases.Current;
        if (current is { IsConnected: true })
        {
            return current;
        }

        if (current is not null)
        {
            await _leases.RetireBrowserAsync(current, "browser is not connected").ConfigureAwait(false);
        }

        if (_playwright is null)
        {
            _playwright = await BoundedBrowserOperation.RunAsync(
                Microsoft.Playwright.Playwright.CreateAsync(),
                _setupTimeout,
                cancellationToken,
                reclaim: playwright =>
                {
                    try
                    {
                        playwright.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error disposing a late Playwright driver");
                    }

                    return Task.CompletedTask;
                },
                observe: LogObserved,
                operationName: "create-playwright-driver",
                reclaimTimeout: _cleanupTimeout).ConfigureAwait(false);
        }

        _logger.LogInformation("Launching Chromium");
        var browser = await BoundedBrowserOperation.RunAsync(
            _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Timeout = (float)_launchTimeout.TotalMilliseconds,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-dev-shm-usage",
                    "--disable-infobars",
                    "--disable-extensions",
                    "--disable-background-networking",
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-breakpad",
                    "--disable-client-side-phishing-detection",
                    "--disable-component-extensions-with-background-pages",
                    "--disable-component-update",
                    "--disable-default-apps",
                    "--disable-domain-reliability",
                    "--disable-features=AudioServiceOutOfProcess,IsolateOrigins,site-per-process,TranslateUI,BlinkGenPropertyTrees",
                    "--disable-hang-monitor",
                    "--disable-ipc-flooding-protection",
                    "--disable-notifications",
                    "--disable-offer-store-unmasked-wallet-cards",
                    "--disable-popup-blocking",
                    "--disable-prompt-on-repost",
                    "--disable-renderer-backgrounding",
                    "--disable-sync",
                    "--force-color-profile=srgb",
                    "--metrics-recording-only",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--no-pings",
                    "--password-store=basic",
                    "--use-mock-keychain",
                    "--enable-automation=false",
                    "--exclude-switches=enable-automation",
                    "--disable-features=UserAgentClientHint",
                    "--enable-unsafe-swiftshader",
                    "--use-gl=swiftshader"
                }
            }),
            _launchTimeout,
            cancellationToken,
            reclaim: launched => _leases.CloseLateBrowserAsync(launched, "cancelled Chromium launch"),
            observe: LogObserved,
            operationName: "launch-chromium",
            reclaimTimeout: _cleanupTimeout).ConfigureAwait(false);

        browser.Disconnected += (_, _) =>
        {
            _logger.LogWarning("Chromium disconnected; the next capture will recreate it");
        };

        try
        {
            _leases.SetCurrent(browser);
        }
        catch
        {
            await _leases.CloseLateBrowserAsync(browser, "browser could not be adopted").ConfigureAwait(false);
            throw;
        }

        return browser;
    }

    private async Task ClosePageBoundedAsync(IPage page, string requestId)
    {
        try
        {
            await BoundedBrowserOperation.RunAsync(
                page.CloseAsync(),
                _cleanupTimeout,
                CancellationToken.None,
                LogObserved,
                $"close rejected page (request {requestId})").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Page cleanup for request {RequestId} did not complete cleanly", requestId);
        }
    }

    private static async Task CloseBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The browser is already unusable; disposal is best-effort because
                // the coordinator bounds and observes this task.
            }
        }
    }

    private static bool IsRecoverableContextFailure(Exception exception) =>
        exception is PlaywrightException or BrowserOperationTimeoutException or BrowserSetupException;

    private static TimeSpan ReadBudget(IConfiguration configuration, string key, int defaultSeconds)
    {
        var seconds = configuration.GetValue<int?>(key) ?? defaultSeconds;
        if (seconds is < MinBudgetSeconds or > MaxBudgetSeconds)
        {
            throw new InvalidOperationException(
                $"{key} must be between {MinBudgetSeconds} and {MaxBudgetSeconds} seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private void LogObserved(string message, Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("{BrowserMessage}", message);
        }
        else
        {
            _logger.LogWarning(exception, "{BrowserMessage}", message);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _browserLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _leases.ReleaseAllAsync("browser service disposed").ConfigureAwait(false);

            try
            {
                _playwright?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing Playwright driver");
            }
            finally
            {
                _playwright = null;
            }
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private static string GetAntiDetectionScript()
    {
        return @"
            (function() {
                'use strict';
                Object.defineProperty(navigator, 'webdriver', { get: () => false, configurable: true });
                try { delete navigator.__proto__.webdriver; } catch (e) {}
                window.chrome = { runtime: {}, loadTimes: function() {}, csi: function() {}, app: {} };
                Object.defineProperty(navigator, 'plugins', {
                    get: () => {
                        const plugins = [];
                        for (let i = 0; i < 3; i++) {
                            plugins.push({
                                0: { type: 'application/x-google-chrome-pdf', suffixes: 'pdf', description: 'Portable Document Format' },
                                description: 'Portable Document Format',
                                filename: 'internal-pdf-viewer',
                                length: 1,
                                name: 'Chrome PDF Plugin'
                            });
                        }
                        return plugins;
                    },
                    configurable: true
                });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-AU', 'en', 'en-US'], configurable: true });
                Object.defineProperty(navigator, 'language', { get: () => 'en-AU', configurable: true });
                Object.defineProperty(navigator, 'vendor', { get: () => 'Google Inc.', configurable: true });
                Object.defineProperty(navigator, 'platform', { get: () => 'Win32', configurable: true });
                Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8, configurable: true });
                if ('deviceMemory' in navigator) {
                    Object.defineProperty(navigator, 'deviceMemory', { get: () => 8, configurable: true });
                }
                Object.defineProperty(navigator, 'connection', {
                    get: () => ({
                        effectiveType: '4g', rtt: 50, downlink: 10, saveData: false, onchange: null,
                        addEventListener: function addEventListener() {},
                        removeEventListener: function removeEventListener() {},
                        dispatchEvent: function dispatchEvent() { return true; }
                    }),
                    configurable: true
                });
                const originalQuery = window.navigator.permissions.query.bind(window.navigator.permissions);
                window.navigator.permissions.query = function query(parameters) {
                    return parameters.name === 'notifications' ?
                        Promise.resolve({ state: Notification.permission }) :
                        originalQuery(parameters);
                };
                if ('getBattery' in navigator) {
                    navigator.getBattery = function getBattery() {
                        return Promise.resolve({
                            charging: true, chargingTime: 0, dischargingTime: Infinity, level: 1,
                            onchargingchange: null, onchargingtimechange: null, ondischargingtimechange: null, onlevelchange: null,
                            addEventListener: function addEventListener() {},
                            removeEventListener: function removeEventListener() {},
                            dispatchEvent: function dispatchEvent() { return true; }
                        });
                    };
                }
            })();
        ";
    }
}
