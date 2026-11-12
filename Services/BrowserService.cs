using BomLocalService.Services.Interfaces;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public sealed class BrowserService : IBrowserService
{
    private readonly ILogger<BrowserService> _logger;
    private readonly string _timezone;
    private readonly IDebugService _debugService;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
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
    }

    public bool IsBrowserConnected
    {
        get
        {
            try
            {
                return !_disposed && _browser?.IsConnected == true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            await DisposeBrowserLockedAsync();

            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
            _logger.LogInformation("Launching Chromium");
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
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
            });

            _browser.Disconnected += (_, _) =>
            {
                _logger.LogWarning("Chromium disconnected; the next capture will recreate it");
            };

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var browser = await GetBrowserAsync(cancellationToken);
            try
            {
                return await browser.NewContextAsync(CreateContextOptions());
            }
            catch (PlaywrightException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Failed to create browser context; recreating Chromium and retrying once");
                await ResetCoreAsync(browser, cancellationToken);
            }
        }

        throw new InvalidOperationException("Could not create a browser context after recreating Chromium");
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

    public async Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context,
        string requestId)
    {
        var page = await context.NewPageAsync();
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

        await page.AddInitScriptAsync(GetAntiDetectionScript());
        return (page, consoleMessages, networkRequests);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Pre-warming browser");
        try
        {
            await GetBrowserAsync(cancellationToken);
            _logger.LogInformation("Browser initialized and ready");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pre-warm browser, will initialize on first use");
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogInformation("Resetting Chromium browser");
            await DisposeBrowserLockedAsync();
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task ResetCoreAsync(IBrowser expected, CancellationToken cancellationToken)
    {
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            if (ReferenceEquals(_browser, expected))
            {
                await DisposeBrowserLockedAsync();
            }
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task DisposeBrowserLockedAsync()
    {
        var browser = _browser;
        _browser = null;
        if (browser == null)
        {
            return;
        }

        try
        {
            await browser.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing Chromium");
        }
        finally
        {
            try
            {
                await browser.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing Chromium");
            }
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
        await _browserLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposeBrowserLockedAsync();
            _playwright?.Dispose();
            _playwright = null;
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
