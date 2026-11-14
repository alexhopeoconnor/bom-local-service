using BomLocalService.Services;
using BomLocalService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BomLocalService.Tests;

/// <summary>
/// Real Chromium checks for the production BrowserService ownership boundary.
/// These tests are gated by BOM_REAL_BROWSER_TESTS=1 and launch the production
/// headed Chromium configuration, so the dedicated run must provide a display
/// (for example Xvfb). Missing prerequisites fail the run rather than skipping.
///
/// Every browser/context created by these tests belongs to the test-owned
/// BrowserService. Cleanup is explicitly bounded; the runner's cgroup cleanup is
/// the outer guarantee. No PID, process-name or shared-runtime signals are used.
/// </summary>
[Trait("Category", "Browser")]
public sealed class RealBrowserLifecycleTests
{
    private const string GateVariable = "BOM_REAL_BROWSER_TESTS";
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OperationBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(180);
    private readonly ITestOutputHelper _output;

    public RealBrowserLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Two_contexts_survive_reset_then_release_and_next_context_succeeds()
    {
        RequirePrerequisites();
        using var deadline = new CancellationTokenSource(TestDeadline);
        var service = CreateBrowserService();
        IBrowserContext? first = null;
        IBrowserContext? second = null;
        IBrowserContext? next = null;

        try
        {
            first = await service.CreateContextAsync(deadline.Token);
            second = await service.CreateContextAsync(deadline.Token);
            var (firstPage, _, _) = await service.CreatePageWithDebugAsync(
                first,
                "real-boundary-first",
                deadline.Token);
            var (secondPage, _, _) = await service.CreatePageWithDebugAsync(
                second,
                "real-boundary-second",
                deadline.Token);

            await SetMarkerAsync(firstPage, "first");
            await SetMarkerAsync(secondPage, "second");

            var originalBrowser = await service.GetBrowserAsync(deadline.Token);
            _output.WriteLine($"Chromium version before reset: {originalBrowser.Version}");

            await service.ResetAsync(deadline.Token);

            // The reset retires the current browser, but leases keep it alive.
            // Both pages must remain usable, not just the context object.
            Assert.Equal("first", await ReadMarkerAsync(firstPage));
            Assert.Equal("second", await ReadMarkerAsync(secondPage));

            Assert.True(
                await ReleaseContextOnPoolAsync(service, first).WaitAsync(CleanupBound),
                "first context should close cleanly after reset");
            first = null;

            Assert.True(
                await ReleaseContextOnPoolAsync(service, second).WaitAsync(CleanupBound),
                "second context should close cleanly after reset");
            second = null;

            next = await service.CreateContextAsync(deadline.Token);
            var (nextPage, _, _) = await service.CreatePageWithDebugAsync(
                next,
                "real-boundary-next",
                deadline.Token);
            await SetMarkerAsync(nextPage, "next");
            Assert.Equal("next", await ReadMarkerAsync(nextPage));

            var recreatedBrowser = await service.GetBrowserAsync(deadline.Token);
            Assert.NotSame(originalBrowser, recreatedBrowser);
            Assert.True(recreatedBrowser.IsConnected, "recreated Chromium should be connected");
            _output.WriteLine($"Chromium version after release/recreation: {recreatedBrowser.Version}");

            Assert.True(
                await ReleaseContextOnPoolAsync(service, next).WaitAsync(CleanupBound),
                "next context should close cleanly");
            next = null;
        }
        finally
        {
            await SafeReleaseContextAsync(service, next);
            await SafeReleaseContextAsync(service, second);
            await SafeReleaseContextAsync(service, first);
            await SafeDisposeServiceAsync(service);
        }
    }

    [Fact]
    public async Task Caller_cancellation_during_delayed_page_releases_capacity()
    {
        RequirePrerequisites();
        using var deadline = new CancellationTokenSource(TestDeadline);
        using var cancellation = new CancellationTokenSource();
        var service = CreateBrowserService();
        IBrowserContext? context = null;
        IBrowserContext? next = null;

        try
        {
            context = await service.CreateContextAsync(deadline.Token);
            var (page, _, _) = await service.CreatePageWithDebugAsync(
                context,
                "real-boundary-delayed",
                deadline.Token);

            var routed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/real-boundary-delayed", async route =>
            {
                routed.TrySetResult(true);
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // Leave the request pending: the context release aborts it.
                }
            });

            var navigation = page.GotoAsync(
                "https://bom.invalid/real-boundary-delayed",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 0
                });
            await routed.Task.WaitAsync(OperationBound);

            Task<bool>? releaseTask = null;
            using var registration = cancellation.Token.Register(() =>
            {
                releaseTask = ReleaseContextOnPoolAsync(service, context!);
            });

            cancellation.Cancel();
            Assert.NotNull(releaseTask);
            Assert.True(
                await releaseTask!.WaitAsync(CleanupBound),
                "cancelled context should release within the cleanup bound");
            context = null;

            await ObserveExpectedPageFailureAsync(navigation, "delayed navigation after cancellation");
            _output.WriteLine("Cancellation released the delayed page's context capacity.");

            next = await service.CreateContextAsync(deadline.Token);
            var (nextPage, _, _) = await service.CreatePageWithDebugAsync(
                next,
                "real-boundary-after-cancellation",
                deadline.Token);
            await SetMarkerAsync(nextPage, "after-cancellation");
            Assert.Equal("after-cancellation", await ReadMarkerAsync(nextPage));

            Assert.True(
                await ReleaseContextOnPoolAsync(service, next).WaitAsync(CleanupBound),
                "next context should close cleanly");
            next = null;
        }
        finally
        {
            await SafeReleaseContextAsync(service, next);
            await SafeReleaseContextAsync(service, context);
            await SafeDisposeServiceAsync(service);
        }
    }

    [Fact]
    public async Task Browser_crash_disconnects_exact_owned_browser_and_service_recreates()
    {
        RequirePrerequisites();
        using var deadline = new CancellationTokenSource(TestDeadline);
        var service = CreateBrowserService();
        IBrowserContext? context = null;
        IBrowserContext? next = null;

        try
        {
            context = await service.CreateContextAsync(deadline.Token);
            var (page, _, _) = await service.CreatePageWithDebugAsync(
                context,
                "real-boundary-crash",
                deadline.Token);
            await SetMarkerAsync(page, "before-crash");

            var browser = await service.GetBrowserAsync(deadline.Token);
            _output.WriteLine($"Chromium version before Browser.crash: {browser.Version}");

            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Disconnected += (_, _) => disconnected.TrySetResult(true);

            var cdp = await browser.NewBrowserCDPSessionAsync().WaitAsync(OperationBound);
            var crashSend = cdp.SendAsync("Browser.crash");
            try
            {
                await crashSend.WaitAsync(OperationBound);
            }
            catch (PlaywrightException ex) when (IsCrashUnsupported(ex))
            {
                Assert.Fail(
                    "Browser.crash is not supported by this Playwright/Chromium runtime; " +
                    $"abrupt-crash proof is unavailable, not passed: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                ObserveLateFault(crashSend);
                _output.WriteLine(
                    "Browser.crash produced no CDP response before the bound; " +
                    $"waiting for the disconnect event: {ex.Message}");
            }
            catch (PlaywrightException ex)
            {
                _output.WriteLine(
                    "Browser.crash ended the CDP connection before a response (expected for a crash): " +
                    ex.Message);
            }

            var disconnectedObserved = await WaitForDisconnectAsync(disconnected.Task, DisconnectBound);
            Assert.True(
                disconnectedObserved,
                "Browser.crash was accepted but no Disconnected event arrived within the bound; " +
                "abrupt-crash proof is unverified, not passed.");
            Assert.False(browser.IsConnected, "the crashed browser must report disconnected");

            var postCrashRelease = await TryReleaseContextAsync(service, context);
            _output.WriteLine($"Post-crash context release clean={FormatClean(postCrashRelease)}");
            if (postCrashRelease.HasValue)
            {
                context = null;
            }

            next = await service.CreateContextAsync(deadline.Token);
            var (nextPage, _, _) = await service.CreatePageWithDebugAsync(
                next,
                "real-boundary-after-crash",
                deadline.Token);
            await SetMarkerAsync(nextPage, "after-crash");
            Assert.Equal("after-crash", await ReadMarkerAsync(nextPage));

            var recreatedBrowser = await service.GetBrowserAsync(deadline.Token);
            Assert.NotSame(browser, recreatedBrowser);
            Assert.True(recreatedBrowser.IsConnected, "recreated Chromium should be connected after a crash");
            _output.WriteLine($"Chromium version after crash recreation: {recreatedBrowser.Version}");

            Assert.True(
                await ReleaseContextOnPoolAsync(service, next).WaitAsync(CleanupBound),
                "context created after crash should close cleanly");
            next = null;
        }
        finally
        {
            await SafeReleaseContextAsync(service, next);
            await SafeReleaseContextAsync(service, context);
            await SafeDisposeServiceAsync(service);
        }
    }

    [Fact]
    public async Task Browser_graceful_close_disconnects_exact_owned_browser_and_service_recreates()
    {
        RequirePrerequisites();
        using var deadline = new CancellationTokenSource(TestDeadline);
        var service = CreateBrowserService();
        IBrowserContext? context = null;
        IBrowserContext? next = null;

        try
        {
            context = await service.CreateContextAsync(deadline.Token);
            var (page, _, _) = await service.CreatePageWithDebugAsync(
                context,
                "real-boundary-graceful",
                deadline.Token);
            await SetMarkerAsync(page, "before-close");

            var browser = await service.GetBrowserAsync(deadline.Token);
            _output.WriteLine($"Chromium version before graceful close: {browser.Version}");

            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Disconnected += (_, _) => disconnected.TrySetResult(true);

            Func<Task> closeOperation = () => browser.CloseAsync();
            var closeTask = Task.Run(closeOperation);
            try
            {
                await closeTask.WaitAsync(DisconnectBound);
            }
            catch (TimeoutException)
            {
                ObserveLateFault(closeTask);
                throw;
            }

            Assert.True(
                await WaitForDisconnectAsync(disconnected.Task, DisconnectBound),
                "graceful Browser.CloseAsync did not produce a bounded Disconnected event");
            Assert.False(browser.IsConnected, "the gracefully closed browser must report disconnected");

            var postCloseRelease = await TryReleaseContextAsync(service, context);
            _output.WriteLine($"Post-close context release clean={FormatClean(postCloseRelease)}");
            if (postCloseRelease.HasValue)
            {
                context = null;
            }

            next = await service.CreateContextAsync(deadline.Token);
            var (nextPage, _, _) = await service.CreatePageWithDebugAsync(
                next,
                "real-boundary-after-close",
                deadline.Token);
            await SetMarkerAsync(nextPage, "after-graceful-close");
            Assert.Equal("after-graceful-close", await ReadMarkerAsync(nextPage));

            var recreatedBrowser = await service.GetBrowserAsync(deadline.Token);
            Assert.NotSame(browser, recreatedBrowser);
            Assert.True(recreatedBrowser.IsConnected, "recreated Chromium should be connected after graceful close");
            _output.WriteLine($"Chromium version after graceful-close recreation: {recreatedBrowser.Version}");

            Assert.True(
                await ReleaseContextOnPoolAsync(service, next).WaitAsync(CleanupBound),
                "context created after graceful close should close cleanly");
            next = null;
        }
        finally
        {
            await SafeReleaseContextAsync(service, next);
            await SafeReleaseContextAsync(service, context);
            await SafeDisposeServiceAsync(service);
        }
    }

    private static BrowserService CreateBrowserService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Timezone"] = "Australia/Brisbane",
                ["Browser:LaunchTimeoutSeconds"] = "60",
                ["Browser:SetupTimeoutSeconds"] = "30",
                ["Browser:OperationTimeoutSeconds"] = "15",
                ["Browser:CleanupTimeoutSeconds"] = "5"
            })
            .Build();

        return new BrowserService(
            NullLogger<BrowserService>.Instance,
            configuration,
            new DisabledDebugService());
    }

    private static void RequirePrerequisites()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Fail(
                $"{GateVariable}=1 is required for Category=Browser real-browser lifecycle checks.");
        }

        if (OperatingSystem.IsLinux() &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Fail(
                "The production BrowserService launches headed Chromium. Set DISPLAY " +
                "(for example through xvfb-run) for the dedicated real-browser run.");
        }
    }

    private static async Task SetMarkerAsync(IPage page, string value)
    {
        await page.SetContentAsync(
            $"<html><body><main id=\"marker\">{value}</main></body></html>")
            .WaitAsync(OperationBound);
    }

    private static async Task<string> ReadMarkerAsync(IPage page)
    {
        return await page.Locator("#marker").InnerTextAsync().WaitAsync(OperationBound);
    }

    private async Task ObserveExpectedPageFailureAsync(Task operation, string description)
    {
        var completed = false;
        try
        {
            await operation.WaitAsync(OperationBound);
            completed = true;
        }
        catch (TimeoutException)
        {
            // Keep the late fault observed; the context is already closed. A
            // stuck native page operation is not claimed solved here.
            _ = operation.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _output.WriteLine(
                $"{description} did not complete within the explicit bound; " +
                "capacity release remains proven by the next context, not by task cleanup.");
        }
        catch (PlaywrightException ex)
        {
            _output.WriteLine($"{description} failed as expected: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine($"{description} was cancelled as expected.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"{description} failed as expected: {ex.GetType().Name}: {ex.Message}");
        }

        if (completed)
        {
            Assert.Fail($"{description} unexpectedly completed after its context was released.");
        }
    }

    private static async Task<bool> WaitForDisconnectAsync(Task disconnected, TimeSpan timeout)
    {
        try
        {
            await disconnected.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static bool IsCrashUnsupported(PlaywrightException exception)
    {
        var message = exception.Message;
        return message.Contains("wasn't found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown method", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid method", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not supported", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<bool> ReleaseContextOnPoolAsync(
        BrowserService service,
        IBrowserContext context) =>
        Task.Run(async () => await service.ReleaseContextAsync(context, CancellationToken.None));

    private async Task<bool?> TryReleaseContextAsync(BrowserService service, IBrowserContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var release = ReleaseContextOnPoolAsync(service, context);
        try
        {
            return await release.WaitAsync(CleanupBound);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(release);
            _output.WriteLine(
                "Post-fault context release exceeded the explicit bound; " +
                "runner outer cleanup remains the proof.");
            return null;
        }
        catch (Exception ex)
        {
            _output.WriteLine(
                $"Post-fault context release failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task SafeReleaseContextAsync(BrowserService service, IBrowserContext? context)
    {
        if (context is null)
        {
            return;
        }

        var release = ReleaseContextOnPoolAsync(service, context);
        try
        {
            var clean = await release.WaitAsync(CleanupBound);
            _output.WriteLine($"Bounded cleanup context release clean={clean}.");
        }
        catch (TimeoutException)
        {
            ObserveLateFault(release);
            _output.WriteLine(
                "Bounded cleanup context release exceeded the explicit bound; " +
                "runner outer cleanup remains the proof.");
        }
        catch (Exception ex)
        {
            _output.WriteLine(
                $"Bounded cleanup context release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SafeDisposeServiceAsync(BrowserService service)
    {
        Func<Task> disposeOperation = () => service.DisposeAsync().AsTask();
        var disposal = Task.Run(disposeOperation);
        try
        {
            await disposal.WaitAsync(CleanupBound);
            _output.WriteLine("BrowserService disposal completed within the explicit bound.");
        }
        catch (TimeoutException)
        {
            ObserveLateFault(disposal);
            _output.WriteLine(
                "BrowserService disposal exceeded the explicit bound; " +
                "runner outer cleanup remains the proof.");
        }
        catch (Exception ex)
        {
            _output.WriteLine(
                $"BrowserService disposal failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatClean(bool? clean) => clean?.ToString() ?? "timeout-or-failure";

    private sealed class DisabledDebugService : IDebugService
    {
        public bool IsEnabled => false;

        public string CreateRequestFolder(string requestId) => string.Empty;

        public Task SaveStepDebugAsync(
            string requestFolder,
            int stepNumber,
            string stepName,
            IPage page,
            List<(string type, string text, DateTime timestamp)>? consoleMessages = null,
            List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveErrorDebugAsync(
            string requestFolder,
            string errorMessage,
            IPage? page = null,
            List<(string type, string text, DateTime timestamp)>? consoleMessages = null,
            List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
