using System.Text.Json;
using BomLocalService.Tests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace BomLocalService.Tests;

/// <summary>
/// Real-browser demo checks for the refresh/retry lifecycle. These are browser
/// integration checks, not live BOM verification: every API response is
/// intercepted so outage, stale status, history ordering and replacement
/// behaviour stay deterministic.
///
/// The dedicated demo job sets BOM_DEMO_BASE_URL against a locally started
/// service. Running the class without that variable must fail rather than
/// silently pass; deterministic suites filter on Category=Browser.
/// </summary>
[Trait("Category", "Browser")]
public class DemoBrowserTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Demo_shows_cold_start_404_then_recovers()
    {
        var baseUrl = RequireBaseUrl();
        var api = new DemoApi(TestImageUrl()) { ColdStartRemaining = 1 };

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // The structured 404 body supplies the display message; no generic text.
        var coldMessage = await WaitForTextAsync(page, "#error-container", "Cache update has been triggered", 15000);
        Assert.Contains("retry in a few moments", coldMessage, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("#frame-slider").CountAsync());
        Assert.True(await page.Locator("#play-btn").IsDisabledAsync());

        // The next scheduled poll recovers without a page reload.
        await WaitForSliderMaxAsync(page, "6");
        await page.Locator("#radar-image").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });
        await WaitForConditionAsync(
            async () => string.IsNullOrEmpty((await page.Locator("#error-container").InnerTextAsync()).Trim()),
            15000,
            "cold-start message should clear after recovery");
        Assert.False(await page.Locator("#play-btn").IsDisabledAsync());

        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    [Fact]
    public async Task Demo_recovers_from_network_outage_and_restarts_playback()
    {
        var baseUrl = RequireBaseUrl();
        var imageUrl = TestImageUrl();
        var api = new DemoApi(imageUrl);

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await WaitForSliderMaxAsync(page, "6");
        await page.Locator("#radar-image").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        await page.Locator("#play-btn").ClickAsync();
        await page.WaitForTimeoutAsync(900);
        Assert.Contains("Pause", await page.Locator("#play-btn").InnerTextAsync(), StringComparison.Ordinal);

        // Outage: the cached frame and controls stay, the failure text is shown.
        api.AbortLatest = true;
        var outageText = await WaitForTextAsync(page, "#error-container", "API connection lost", 12000);
        Assert.Contains("Attempt 1/10", outageText, StringComparison.Ordinal);

        // Backoff must not spin: no retry before the five-second base delay.
        var outageRequests = api.LatestStats().Requests;
        await page.WaitForTimeoutAsync(1200);
        Assert.Equal(outageRequests, api.LatestStats().Requests);
        Assert.Contains(
            "API connection lost",
            await page.Locator("#error-container").InnerTextAsync(),
            StringComparison.Ordinal);
        Assert.True(await page.Locator("#radar-image").IsVisibleAsync(), "cached frame should remain visible during an outage");
        Assert.Equal("6", await page.Locator("#frame-slider").GetAttributeAsync("max"));

        // Recovery retry must apply the full success state, not just metadata.
        api.AbortLatest = false;
        await WaitForTextAsync(page, "#refresh-status", "Active", 15000);
        await WaitForConditionAsync(
            async () => string.IsNullOrEmpty((await page.Locator("#error-container").InnerTextAsync()).Trim()),
            15000,
            "error banner should clear after a successful retry");

        Assert.Equal("6", await page.Locator("#frame-slider").GetAttributeAsync("max"));
        Assert.Contains("Pause", await page.Locator("#play-btn").InnerTextAsync(), StringComparison.Ordinal);
        Assert.True(await page.Locator("#frame-first-btn").IsVisibleAsync(), "frame controls must be rebuilt after recovery");

        var before = int.Parse(await page.Locator("#frame-jump-input").InputValueAsync());
        await page.WaitForTimeoutAsync(700);
        var after = int.Parse(await page.Locator("#frame-jump-input").InputValueAsync());
        Assert.NotEqual(before, after);

        var stats = api.LatestStats();
        Assert.Equal(1, stats.MaxConcurrent);
        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    [Fact]
    public async Task Demo_preserves_stale_failure_detail_through_relative_updates()
    {
        var baseUrl = RequireBaseUrl();
        var api = new DemoApi(TestImageUrl())
        {
            LatestCacheValid = false,
            StatusUpdateFailed = true
        };

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await WaitForSliderMaxAsync(page, "6");
        var detail = await WaitForTextAsync(page, "#update-status-detail", "Last update failed");
        Assert.Contains("CAPTURE_FAILED", detail, StringComparison.Ordinal);
        Assert.Contains("Retry after", detail, StringComparison.Ordinal);

        // The one-second relative-time tick must not replace the failure text.
        await page.WaitForTimeoutAsync(1400);
        Assert.Contains(
            "Last update failed",
            await page.Locator("#update-status-detail").InnerTextAsync(),
            StringComparison.Ordinal);
        Assert.Equal("Radar capture failed", await page.Locator("#update-status-detail").GetAttributeAsync("title"));
        Assert.Contains("Stale", await page.Locator("#cache-status").InnerTextAsync(), StringComparison.Ordinal);

        // A later healthy response clears the failure state.
        api.StatusUpdateFailed = false;
        api.LatestCacheValid = true;
        await SaveSettingsAsync(page);
        await WaitForTextAsync(page, "#update-status-detail", "Cache is valid");
        await page.WaitForTimeoutAsync(1200);
        Assert.Contains(
            "Cache is valid",
            await page.Locator("#update-status-detail").InnerTextAsync(),
            StringComparison.Ordinal);
        Assert.Contains("ACTIVE", await page.Locator("#cache-status").InnerTextAsync(), StringComparison.Ordinal);

        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    [Fact]
    public async Task Demo_renders_every_history_frame_in_chronological_order()
    {
        var baseUrl = RequireBaseUrl();
        var api = new DemoApi(TestImageUrl());

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForSliderMaxAsync(page, "6");

        // Historical groups arrive out of order across overlapping captures, and
        // one observation appears in two captures; every frame must survive.
        await SaveSettingsAsync(page, "1h");
        // Jump controls only exist in extended mode, so they distinguish the
        // history collection from the previous (also 7-frame) latest collection.
        await WaitForConditionAsync(
            async () => await page.Locator("#frame-prev-jump-btn").CountAsync() == 1,
            15000,
            "extended-mode jump controls should appear");
        await WaitForSliderMaxAsync(page, "6");

        var expectedObservations = new[]
        {
            Iso(0), Iso(5), Iso(10), Iso(15), Iso(20), Iso(20), Iso(25)
        };
        var expectedText = new List<string>();
        foreach (var observation in expectedObservations)
        {
            expectedText.Add(await FormatAsync(page, observation));
        }

        // Initial selection is already first (and correctly disables First).
        // Exercise both endpoint controls before walking the full chronology.
        await page.Locator("#frame-last-btn").ClickAsync();
        await page.Locator("#frame-first-btn").ClickAsync();
        var actualText = new List<string>();
        for (var index = 0; index < expectedObservations.Length; index++)
        {
            actualText.Add(await page.Locator("#current-frame-time").InnerTextAsync());
            if (index < expectedObservations.Length - 1)
            {
                await page.Locator("#next-btn").ClickAsync();
            }
        }

        for (var index = 0; index < expectedText.Count; index++)
        {
            Assert.Equal(expectedText[index], actualText[index]);
        }

        Assert.Contains("Frame 6 of 6", await page.Locator("#frame-info").InnerTextAsync(), StringComparison.Ordinal);
        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    [Fact]
    public async Task Demo_clamps_shorter_replacement_and_clears_empty_collection()
    {
        var baseUrl = RequireBaseUrl();
        var api = new DemoApi(TestImageUrl());

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        // Latest mode displays relative minutes; keep Date fixed while timers
        // continue to run so playback and replacement remain real browser work.
        await page.Clock.SetFixedTimeAsync("2026-11-12T07:35:00Z");
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForSliderMaxAsync(page, "6");

        await page.Locator("#frame-last-btn").ClickAsync();
        Assert.Equal("5 min ago", await page.Locator("#current-frame-time").InnerTextAsync());

        // A shorter replacement keeps the same observation and a valid index.
        api.LatestFrameCount = 3;
        await SaveSettingsAsync(page);
        await WaitForSliderMaxAsync(page, "2");
        Assert.Equal("2", await page.Locator("#frame-jump-input").InputValueAsync());
        Assert.Equal("5 min ago", await page.Locator("#current-frame-time").InnerTextAsync());

        await page.Locator("#play-btn").ClickAsync();
        await page.WaitForTimeoutAsync(700);
        Assert.Contains("Pause", await page.Locator("#play-btn").InnerTextAsync(), StringComparison.Ordinal);

        // An empty replacement pauses playback and clears frame display.
        api.EmptyLatest = true;
        await SaveSettingsAsync(page);
        await WaitForEmptyControlsAsync(page);

        Assert.Contains("Waiting for cache", await page.Locator("#frame-info").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Contains("No radar frames available", await page.Locator("#error-container").InnerTextAsync(), StringComparison.Ordinal);
        Assert.False(await page.Locator("#radar-image").IsVisibleAsync(), "empty replacement must hide the stale frame");
        await page.WaitForTimeoutAsync(700);
        Assert.Contains("▶ Play", await page.Locator("#play-btn").InnerTextAsync(), StringComparison.Ordinal);
        Assert.True(await page.Locator("#play-btn").IsDisabledAsync(), "playback must stay paused for an empty collection");
        Assert.Equal(0, await page.Locator("#frame-slider").CountAsync());

        // Recovery rebuilds controls and enables playback again.
        api.EmptyLatest = false;
        api.LatestFrameCount = 7;
        await SaveSettingsAsync(page);
        await WaitForSliderMaxAsync(page, "6");
        await page.Locator("#radar-image").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });
        Assert.False(await page.Locator("#play-btn").IsDisabledAsync());

        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    [Fact]
    public async Task Demo_uses_one_poll_owner_without_overlapping_requests()
    {
        var baseUrl = RequireBaseUrl();
        var api = new DemoApi(TestImageUrl())
        {
            DelayLatest = true,
            LatestDelayMs = 250
        };

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await CreateContextAsync(browser, baseUrl);
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };

        await page.RouteAsync("**/api/**", api.HandleAsync);
        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForSliderMaxAsync(page, "6");

        // Exactly one automatic refresh after the five-second interval.
        await WaitForLatestRequestsAsync(api, 2, 15000);
        var quietCount = api.LatestStats().Requests;
        await page.WaitForTimeoutAsync(1200);
        Assert.Equal(quietCount, api.LatestStats().Requests);
        Assert.Equal(1, api.LatestStats().MaxConcurrent);

        // Three concurrent callers coalesce into one in-flight request and one
        // queued follow-up; they must never overlap on the wire.
        var before = api.LatestStats().Requests;
        await page.EvaluateAsync<int>(
            "async () => { const m = await import('/js/radar-test/polling.js'); await Promise.all([m.refreshRadar(), m.refreshRadar(), m.refreshRadar()]); return 1; }");
        await WaitForLatestRequestsAsync(api, before + 2, 10000);
        await page.WaitForTimeoutAsync(600);
        Assert.Equal(before + 2, api.LatestStats().Requests);
        Assert.Equal(1, api.LatestStats().MaxConcurrent);

        // Stopping during a request must not let its completion restart polling.
        await page.EvaluateAsync<int>(
            "async () => { const m = await import('/js/radar-test/polling.js'); const pending = m.refreshRadar(); m.stopPolling(); await pending; return 1; }");
        var stoppedCount = api.LatestStats().Requests;
        Assert.True(await page.EvaluateAsync<bool>(
            "async () => { const { state } = await import('/js/radar-test/state.js'); return !state.pollingEnabled && state.refreshTimeout === null && !state.refreshQueued; }"));
        await page.WaitForTimeoutAsync(5500);
        Assert.Equal(stoppedCount, api.LatestStats().Requests);

        await page.EvaluateAsync<int>(
            "async () => { const m = await import('/js/radar-test/polling.js'); await m.restartPolling(); return 1; }");
        Assert.Equal(stoppedCount + 1, api.LatestStats().Requests);

        AssertNoBrowserErrors(pageErrors, consoleErrors);
    }

    private static string RequireBaseUrl()
    {
        var baseUrl = Environment.GetEnvironmentVariable("BOM_DEMO_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Assert.Fail("BOM_DEMO_BASE_URL must point at a running service for Category=Browser demo checks.");
        }

        return baseUrl!.TrimEnd('/');
    }

    private static string TestImageUrl()
    {
        var png = Convert.ToBase64String(TestPng.Create(30, 120, 210));
        return $"data:image/png;base64,{png}";
    }

    private static Task<IBrowser> LaunchAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
        });

    private static async Task<IBrowserContext> CreateContextAsync(IBrowser browser, string baseUrl)
    {
        var context = await browser.NewContextAsync();
        var settings = new
        {
            frameInterval = 0.2,
            refreshInterval = 5,
            autoPlay = false,
            timespan = "latest"
        };
        await context.AddCookiesAsync(new[]
        {
            new Cookie
            {
                Name = "radarTestSettings",
                Value = Uri.EscapeDataString(JsonSerializer.Serialize(settings, JsonOptions)),
                Url = baseUrl + "/"
            }
        });
        return context;
    }

    private static async Task SaveSettingsAsync(IPage page, string? timespan = null)
    {
        await page.Locator("#settings-btn-header").ClickAsync();
        if (timespan is not null)
        {
            await page.Locator("#timespan-select").SelectOptionAsync(timespan);
        }
        await page.Locator("#save-settings-btn").ClickAsync();
    }

    private static async Task FulfillJsonAsync(IRoute route, int status, object body)
    {
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(body, JsonOptions)
        });
    }

    private static async Task WaitForSliderMaxAsync(IPage page, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.Locator("#frame-slider").GetAttributeAsync("max") == expected)
            {
                return;
            }

            await page.WaitForTimeoutAsync(100);
        }

        Assert.Equal(expected, await page.Locator("#frame-slider").GetAttributeAsync("max"));
    }

    private static async Task<string> WaitForTextAsync(IPage page, string selector, string expected, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            text = await page.Locator(selector).InnerTextAsync();
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return text;
            }

            await page.WaitForTimeoutAsync(100);
        }

        Assert.Contains(expected, text, StringComparison.Ordinal);
        return text;
    }

    private static async Task WaitForEmptyControlsAsync(IPage page, int timeoutMs = 15000)
    {
        await WaitForConditionAsync(
            async () => await page.Locator("#frame-slider").CountAsync() == 0 &&
                        await page.Locator("#play-btn").IsDisabledAsync(),
            timeoutMs,
            "empty replacement should clear controls and disable playback");

        Assert.Equal(0, await page.Locator("#frame-slider").CountAsync());
        Assert.True(await page.Locator("#play-btn").IsDisabledAsync());
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, int timeoutMs, string message)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(await condition(), message);
    }

    private static async Task WaitForLatestRequestsAsync(DemoApi api, int minimum, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (api.LatestStats().Requests >= minimum)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(api.LatestStats().Requests >= minimum, $"expected at least {minimum} latest requests");
    }

    private static void AssertNoBrowserErrors(List<string> pageErrors, List<string> consoleErrors)
    {
        Assert.Empty(pageErrors);
        Assert.DoesNotContain(
            consoleErrors,
            message => message.Contains("Uncaught", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<string> FormatAsync(IPage page, string iso) =>
        page.EvaluateAsync<string>(
            "value => new Date(value).toLocaleString('en-AU', { timeZone: 'Australia/Brisbane', year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })",
            iso);

    private static string Iso(int minute) =>
        new DateTimeOffset(2026, 11, 12, 7, minute, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record HistoryGroup(string Name, string CacheTimestamp, string ObservationTime, string[] FrameTimes);

    private sealed class DemoApi
    {
        private readonly object _sync = new();
        private readonly string _imageUrl;
        private int _latestRequests;
        private int _latestInFlight;
        private int _maxConcurrentLatest;

        public DemoApi(string imageUrl)
        {
            _imageUrl = imageUrl;
        }

        public bool AbortLatest { get; set; }
        public int ColdStartRemaining { get; set; }
        public bool DelayLatest { get; set; }
        public int LatestDelayMs { get; set; } = 250;
        public int LatestFrameCount { get; set; } = 7;
        public bool EmptyLatest { get; set; }
        public bool LatestCacheValid { get; set; } = true;
        public bool StatusUpdateFailed { get; set; }
        public string StatusErrorCode { get; set; } = "CAPTURE_FAILED";
        public string StatusError { get; set; } = "Radar capture failed";
        public bool StatusIsUpdating { get; set; }
        public DateTimeOffset? NextRetryAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);

        public List<HistoryGroup> History { get; } = new()
        {
            new("A", Iso(10), Iso(10), new[] { Iso(0), Iso(10) }),
            new("B", Iso(25), Iso(25), new[] { Iso(5), Iso(15), Iso(25) }),
            new("C", Iso(20), Iso(20), new[] { Iso(20) }),
            new("D", Iso(20), Iso(20), new[] { Iso(20) })
        };

        public (int Requests, int MaxConcurrent) LatestStats()
        {
            lock (_sync)
            {
                return (_latestRequests, _maxConcurrentLatest);
            }
        }

        public async Task HandleAsync(IRoute route)
        {
            var path = new Uri(route.Request.Url).AbsolutePath;

            if (path.EndsWith("/refresh", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, new { updateTriggered = true });
                return;
            }

            if (path.EndsWith("/range", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, new
                {
                    oldestCache = new { folderName = "A", cacheTimestamp = Iso(0), observationTime = Iso(10) },
                    newestCache = new { folderName = "B", cacheTimestamp = Iso(25), observationTime = Iso(25) },
                    totalCacheFolders = 4,
                    timeSpanMinutes = 25,
                    oldestObservationTime = Iso(0),
                    newestObservationTime = Iso(25)
                });
                return;
            }

            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, new
                {
                    cacheExists = true,
                    cacheIsValid = LatestCacheValid,
                    isUpdating = StatusIsUpdating,
                    updateFailed = StatusUpdateFailed,
                    errorCode = StatusUpdateFailed ? StatusErrorCode : null,
                    error = StatusUpdateFailed ? StatusError : null,
                    nextRetryAt = StatusUpdateFailed ? NextRetryAt?.ToString("O") : null,
                    consecutiveFailures = StatusUpdateFailed ? 2 : 0,
                    queueDepth = 0,
                    nextUpdateTime = "2026-11-12T07:42:30Z"
                });
                return;
            }

            if (path.EndsWith("/timeseries", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, BuildTimeseriesPayload());
                return;
            }

            if (path.EndsWith("/api/radar/Pomona/QLD", StringComparison.Ordinal))
            {
                await HandleLatestAsync(route);
                return;
            }

            await FulfillJsonAsync(route, 200, new { });
        }

        private async Task HandleLatestAsync(IRoute route)
        {
            lock (_sync)
            {
                _latestRequests++;
                _latestInFlight++;
                _maxConcurrentLatest = Math.Max(_maxConcurrentLatest, _latestInFlight);
            }

            try
            {
                if (DelayLatest)
                {
                    await Task.Delay(LatestDelayMs);
                }

                if (AbortLatest)
                {
                    await route.AbortAsync("failed");
                    return;
                }

                if (ColdStartRemaining > 0)
                {
                    ColdStartRemaining--;
                    await FulfillJsonAsync(route, 404, BuildColdStartPayload());
                    return;
                }

                var observations = EmptyLatest
                    ? Array.Empty<string>()
                    : BuildObservations(LatestFrameCount);
                await FulfillJsonAsync(route, 200, BuildLatestPayload(observations));
            }
            finally
            {
                lock (_sync)
                {
                    _latestInFlight--;
                }
            }
        }

        private object BuildColdStartPayload() => new
        {
            errorCode = "CACHE_NOT_FOUND",
            message = "No cached data found for this location. Cache update has been triggered in background. Please retry in a few moments.",
            details = new
            {
                cacheExists = false,
                cacheIsValid = false,
                updateTriggered = true,
                statusMessage = "No cache exists"
            },
            suggestions = new
            {
                refreshEndpoint = "/api/cache/Pomona/QLD/refresh",
                retryAfter = 5
            }
        };

        private object BuildLatestPayload(string[] observations)
        {
            var frames = observations.Select((time, index) => new
            {
                frameIndex = index,
                absoluteObservationTime = time,
                imageUrl = _imageUrl
            }).ToArray();

            return new
            {
                frames,
                observationTime = observations.Length > 0 ? observations[^1] : Iso(30),
                forecastTime = "2026-11-12T06:00:00Z",
                weatherStation = "Test Station",
                distance = "10 km",
                cacheIsValid = LatestCacheValid,
                cacheExpiresAt = "2026-11-12T07:42:30Z",
                isUpdating = false,
                nextUpdateTime = "2026-11-12T07:42:30Z",
                totalFrames = observations.Length
            };
        }

        private object BuildTimeseriesPayload()
        {
            var folders = History.Select(group => new
            {
                cacheFolderName = group.Name,
                cacheTimestamp = group.CacheTimestamp,
                observationTime = group.ObservationTime,
                frames = group.FrameTimes.Select((time, index) => new
                {
                    frameIndex = index,
                    absoluteObservationTime = time,
                    imageUrl = _imageUrl
                }).ToArray()
            }).ToArray();

            return new
            {
                cacheFolders = folders,
                startTime = "2026-11-12T06:00:00Z",
                endTime = "2026-11-12T07:30:00Z",
                totalFrames = History.Sum(group => group.FrameTimes.Length)
            };
        }

        private static string[] BuildObservations(int count)
        {
            var startMinute = count == 3 ? 20 : 0;
            return Enumerable.Range(0, count)
                .Select(index => Iso(startMinute + index * 5))
                .ToArray();
        }
    }
}
