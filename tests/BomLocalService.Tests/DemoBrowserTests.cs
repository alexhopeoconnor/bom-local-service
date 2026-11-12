using System.Text.Json;
using BomLocalService.Tests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace BomLocalService.Tests;

/// <summary>
/// Real-browser demo check. Skipped by default; CI runs it against a running
/// service with BOM_DEMO_BASE_URL set. API responses are intercepted so the
/// test is deterministic and never depends on upstream weather.
/// </summary>
public class DemoBrowserTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Demo_handles_cold_start_playback_overlap_shorter_replacement_and_recovery()
    {
        var baseUrl = Environment.GetEnvironmentVariable("BOM_DEMO_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return; // Required CI checks stay deterministic; the browser job sets this explicitly.
        }

        baseUrl = baseUrl.TrimEnd('/');
        var png = Convert.ToBase64String(TestPng.Create(30, 120, 210));
        var imageUrl = $"data:image/png;base64,{png}";
        var coldRemaining = 1;
        var latestCount = 7;

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
        });

        await using var context = await browser.NewContextAsync();
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

        var page = await context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        };

        await page.RouteAsync("**/api/**", async route =>
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
                    oldestCache = new { folderName = "A", cacheTimestamp = "2026-11-12T07:00:00Z", observationTime = "2026-11-12T07:15:00Z" },
                    newestCache = new { folderName = "B", cacheTimestamp = "2026-11-12T07:20:00Z", observationTime = "2026-11-12T07:20:00Z" },
                    totalCacheFolders = 2,
                    timeSpanMinutes = 20,
                    oldestObservationTime = "2026-11-12T07:00:00Z",
                    newestObservationTime = "2026-11-12T07:20:00Z"
                });
                return;
            }

            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, new
                {
                    cacheExists = true,
                    cacheIsValid = true,
                    isUpdating = false,
                    updateFailed = false,
                    queueDepth = 0
                });
                return;
            }

            if (path.EndsWith("/timeseries", StringComparison.Ordinal))
            {
                await FulfillJsonAsync(route, 200, new
                {
                    cacheFolders = new object[]
                    {
                        new
                        {
                            cacheFolderName = "A",
                            cacheTimestamp = "2026-11-12T07:00:00Z",
                            observationTime = "2026-11-12T07:10:00Z",
                            frames = new object[]
                            {
                                new { frameIndex = 0, absoluteObservationTime = "2026-11-12T07:00:00Z", imageUrl },
                                new { frameIndex = 1, absoluteObservationTime = "2026-11-12T07:10:00Z", imageUrl }
                            }
                        },
                        new
                        {
                            cacheFolderName = "B",
                            cacheTimestamp = "2026-11-12T07:20:00Z",
                            observationTime = "2026-11-12T07:20:00Z",
                            frames = new object[]
                            {
                                new { frameIndex = 0, absoluteObservationTime = "2026-11-12T07:05:00Z", imageUrl },
                                new { frameIndex = 1, absoluteObservationTime = "2026-11-12T07:15:00Z", imageUrl }
                            }
                        }
                    },
                    startTime = "2026-11-12T06:00:00Z",
                    endTime = "2026-11-12T07:30:00Z",
                    totalFrames = 4
                });
                return;
            }

            if (path.EndsWith("/api/radar/Pomona/QLD", StringComparison.Ordinal))
            {
                if (coldRemaining > 0)
                {
                    coldRemaining--;
                    await FulfillJsonAsync(route, 404, new
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
                    });
                    return;
                }

                var observations = latestCount == 7
                    ? new[] { "2026-11-12T07:00:00Z", "2026-11-12T07:05:00Z", "2026-11-12T07:10:00Z", "2026-11-12T07:15:00Z", "2026-11-12T07:20:00Z", "2026-11-12T07:25:00Z", "2026-11-12T07:30:00Z" }
                    : new[] { "2026-11-12T07:20:00Z", "2026-11-12T07:25:00Z", "2026-11-12T07:30:00Z" };

                await FulfillJsonAsync(route, 200, new
                {
                    frames = observations.Select((time, index) => new
                    {
                        frameIndex = index,
                        absoluteObservationTime = time,
                        imageUrl
                    }).ToArray(),
                    observationTime = observations[^1],
                    forecastTime = "2026-11-12T06:00:00Z",
                    weatherStation = "Test Station",
                    distance = "10 km",
                    cacheIsValid = true,
                    cacheExpiresAt = "2026-11-12T07:42:30Z",
                    isUpdating = false,
                    nextUpdateTime = "2026-11-12T07:42:30Z"
                });
                return;
            }

            await FulfillJsonAsync(route, 200, new { });
        });

        await page.GotoAsync($"{baseUrl}/radar/Pomona/QLD", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Cold start uses the structured 404 body once; the demo must show its full message.
        await page.Locator("#error-container .error").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        var coldMessage = await page.Locator("#error-container").InnerTextAsync();
        Assert.Contains("Cache update has been triggered", coldMessage);

        // Next automatic refresh (5s) succeeds and starts playback.
        await page.Locator("#radar-image").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });
        Assert.Contains("ACTIVE", await page.Locator("#cache-status").InnerTextAsync());
        Assert.Equal("6", await page.Locator("#frame-slider").GetAttributeAsync("max"));

        await page.Locator("#play-btn").ClickAsync();
        await page.WaitForTimeoutAsync(1200);
        if ((await page.Locator("#play-btn").InnerTextAsync()).Contains("Pause"))
        {
            await page.Locator("#play-btn").ClickAsync();
        }

        var advancedIndex = int.Parse(await page.Locator("#frame-jump-input").InputValueAsync());
        Assert.True(advancedIndex > 0, "playback did not advance the selected frame");

        // Switch to historical mode with overlapping, out-of-group-order frames.
        await page.Locator("#settings-btn-header").ClickAsync();
        await page.Locator("#timespan-select").SelectOptionAsync("1h");
        await page.Locator("#save-settings-btn").ClickAsync();
        await WaitForSliderMaxAsync(page, "3");

        await page.Locator("#frame-first-btn").ClickAsync();
        var firstText = await page.Locator("#current-frame-time").InnerTextAsync();
        var expectedFirst = await FormatAsync(page, "2026-11-12T07:00:00Z");
        Assert.Equal(expectedFirst, firstText);

        await page.Locator("#frame-last-btn").ClickAsync();
        var lastText = await page.Locator("#current-frame-time").InnerTextAsync();
        var expectedLast = await FormatAsync(page, "2026-11-12T07:15:00Z");
        Assert.Equal(expectedLast, lastText);

        // Replacing the collection with a shorter one must keep the index valid.
        latestCount = 3;
        await page.Locator("#settings-btn-header").ClickAsync();
        await page.Locator("#timespan-select").SelectOptionAsync("latest");
        await page.Locator("#save-settings-btn").ClickAsync();
        await WaitForSliderMaxAsync(page, "2");
        var clampedIndex = int.Parse(await page.Locator("#frame-jump-input").InputValueAsync());
        Assert.InRange(clampedIndex, 0, 2);

        Assert.DoesNotContain(consoleErrors, message => message.Contains("Uncaught", StringComparison.OrdinalIgnoreCase));
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

    private static Task<string> FormatAsync(IPage page, string iso) =>
        page.EvaluateAsync<string>(
            "value => new Date(value).toLocaleString('en-AU', { timeZone: 'Australia/Brisbane', year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })",
            iso);
}
