using BomLocalService.Models;
using BomLocalService.Services;
using BomLocalService.Services.Browser;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace BomLocalService.Tests;

/// <summary>
/// Controlled checks for the browser ownership/deadline boundary. These tests
/// never launch Chromium; they exercise the same helper and coordinator types
/// that production BrowserService/RadarCaptureService use, with narrow fake
/// resources and delegates. Real Chromium evidence is a separate gated check.
/// </summary>
public sealed class BrowserLifecycleTests
{
    [Fact]
    public async Task Delayed_context_setup_cancels_promptly_and_reclaims_late_resource()
    {
        var setup = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimed = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = BoundedBrowserOperation.RunAsync(
            setup.Task,
            TimeSpan.FromSeconds(10),
            cancellation.Token,
            resource =>
            {
                reclaimed.TrySetResult(resource);
                return Task.CompletedTask;
            },
            (_, _) => { },
            "test-context-setup");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        var lateResource = new FakeResource("late-context");
        setup.SetResult(lateResource);

        var adopted = await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(lateResource, adopted);
    }

    [Fact]
    public async Task Late_setup_exception_is_observed_without_faulting_caller()
    {
        var setup = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = BoundedBrowserOperation.RunAsync(
            setup.Task,
            TimeSpan.FromSeconds(10),
            cancellation.Token,
            _ => Task.CompletedTask,
            (_, exception) => observed.TrySetResult(exception),
            "test-late-failure");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        setup.SetException(new InvalidOperationException("late setup failure"));
        var exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task Setup_timeout_is_bounded_and_reclaims_late_resource()
    {
        var setup = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimed = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = BoundedBrowserOperation.RunAsync(
            setup.Task,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None,
            resource =>
            {
                reclaimed.TrySetResult(resource);
                return Task.CompletedTask;
            },
            (_, _) => { },
            "test-timeout");

        var timeout = await Assert.ThrowsAsync<BrowserOperationTimeoutException>(() => operation);
        Assert.Equal("test-timeout", timeout.OperationName);

        var lateResource = new FakeResource("timed-out-context");
        setup.SetResult(lateResource);
        Assert.Same(lateResource, await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Completed_setup_is_reclaimed_once_when_cancellation_is_already_requested()
    {
        var resource = new FakeResource("completed-context");
        var reclaimed = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimCount = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var operation = BoundedBrowserOperation.RunAsync(
            Task.FromResult(resource),
            TimeSpan.FromSeconds(10),
            cancellation.Token,
            created =>
            {
                Interlocked.Increment(ref reclaimCount);
                reclaimed.TrySetResult(created);
                return Task.CompletedTask;
            },
            (_, _) => { },
            "test-adoption-race");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Same(resource, await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref reclaimCount));
    }

    [Fact]
    public async Task Completion_racing_cancellation_reclaims_exactly_once()
    {
        var setup = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimed = new TaskCompletionSource<FakeResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimCount = 0;
        var resource = new FakeResource("racing-context");
        using var cancellation = new CancellationTokenSource();

        var operation = BoundedBrowserOperation.RunAsync(
            setup.Task,
            TimeSpan.FromSeconds(10),
            cancellation.Token,
            completed =>
            {
                Interlocked.Increment(ref reclaimCount);
                reclaimed.TrySetResult(completed);
                return Task.CompletedTask;
            },
            (_, _) => { },
            "test-completion-race");

        cancellation.Cancel();
        setup.SetResult(resource);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Same(resource, await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref reclaimCount));
    }

    [Fact]
    public async Task Blocking_reclaim_is_bounded_and_not_awaited_indefinitely()
    {
        var resource = new FakeResource("completed-context");
        var reclaimStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReclaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var operation = BoundedBrowserOperation.RunAsync(
            Task.FromResult(resource),
            TimeSpan.FromSeconds(10),
            cancellation.Token,
            _ =>
            {
                reclaimStarted.TrySetResult(true);
                releaseReclaim.Task.GetAwaiter().GetResult();
                return Task.CompletedTask;
            },
            (_, _) => { },
            "test-blocking-reclaim",
            reclaimTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(5)));

        await reclaimStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(releaseReclaim.Task.IsCompleted);
        releaseReclaim.SetResult(true);
    }

    [Fact]
    public async Task Concurrent_release_of_one_context_closes_it_once()
    {
        var closeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCount = 0;
        var browser = new FakeBrowser("shared");
        var context = new FakeContext("shared-context");
        var coordinator = CreateCoordinator(
            closeContext: async _ =>
            {
                Interlocked.Increment(ref closeCount);
                closeStarted.TrySetResult(true);
                await releaseClose.Task;
            });

        coordinator.SetCurrent(browser);
        Assert.True(coordinator.Register(context, browser));

        var first = coordinator.ReleaseContextAsync(context);
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.ReleaseContextAsync(context);

        releaseClose.SetResult(true);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref closeCount));
        Assert.Equal(0, coordinator.ActiveContextCount);
    }

    [Fact]
    public async Task Context_close_timeout_is_bounded_retires_browser_and_observes_late_close()
    {
        var closeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCloseCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new List<string>();
        var browser = new FakeBrowser("first");
        var context = new FakeContext("slow-context");
        var coordinator = CreateCoordinator(
            cleanupTimeout: TimeSpan.FromMilliseconds(100),
            closeContext: async _ =>
            {
                await closeGate.Task;
                lateCloseCompleted.TrySetResult(true);
            },
            closeBrowser: closed =>
            {
                closed.RecordClose();
                return Task.CompletedTask;
            },
            observe: (message, _) =>
            {
                lock (observations)
                {
                    observations.Add(message);
                }
            });

        coordinator.SetCurrent(browser);
        Assert.True(coordinator.Register(context, browser));

        var clean = await coordinator.ReleaseContextAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(clean);
        Assert.Equal(1, browser.CloseCount);
        Assert.Null(coordinator.Current);
        Assert.Equal(0, coordinator.ActiveContextCount);
        lock (observations)
        {
            Assert.Contains(observations, message => message.Contains("cleanup budget", StringComparison.OrdinalIgnoreCase));
        }

        closeGate.SetResult(true);
        await lateCloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Two_capture_recovery_does_not_close_a_healthy_context()
    {
        var closedBrowsers = new List<FakeBrowser>();
        var coordinator = CreateCoordinator(
            closeContext: context =>
            {
                context.RecordClose();
                return Task.CompletedTask;
            },
            closeBrowser: browser =>
            {
                lock (closedBrowsers)
                {
                    closedBrowsers.Add(browser);
                    browser.RecordClose();
                }

                return Task.CompletedTask;
            });

        var failedBrowser = new FakeBrowser("affected");
        coordinator.SetCurrent(failedBrowser);
        var failedCapture = new FakeContext("failed-capture");
        var healthyCapture = new FakeContext("healthy-capture");
        Assert.True(coordinator.Register(failedCapture, failedBrowser));
        Assert.True(coordinator.Register(healthyCapture, failedBrowser));

        // A failed capture requests a reset/retirement. The healthy capture must
        // keep its own context and browser alive.
        await coordinator.RetireCurrentAsync("capture failed");
        Assert.Empty(closedBrowsers);
        Assert.Null(coordinator.Current);
        Assert.Equal(1, coordinator.RetiredBrowserCount);

        // The next capture starts on a fresh instance while the healthy capture
        // is still running on the retired one.
        var replacementBrowser = new FakeBrowser("replacement");
        coordinator.SetCurrent(replacementBrowser);
        Assert.Same(replacementBrowser, coordinator.Current);

        Assert.True(await coordinator.ReleaseContextAsync(failedCapture));
        Assert.Empty(closedBrowsers);
        Assert.Equal(1, coordinator.ActiveContextCount);

        // Only the healthy capture's release retires the old instance. That
        // release happens after its capture work has finished.
        Assert.True(await coordinator.ReleaseContextAsync(healthyCapture));
        lock (closedBrowsers)
        {
            Assert.Single(closedBrowsers);
            Assert.Same(failedBrowser, closedBrowsers[0]);
        }

        Assert.Same(replacementBrowser, coordinator.Current);
        Assert.Equal(0, coordinator.ActiveContextCount);
        Assert.Equal(1, failedCapture.CloseCount);
        Assert.Equal(1, healthyCapture.CloseCount);
    }

    [Fact]
    public async Task Capture_boundary_normalizes_playwright_failure_to_cancellation_when_deadline_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var browser = new CancellingBrowserService(cancellation);
        var capture = new RadarCaptureService(
            NullLogger<RadarCaptureService>.Instance,
            browser,
            new UnusedScrapingService(),
            new DisabledDebugService(),
            new FixedCacheService(Path.GetTempPath()));

        Assert.True(LocationId.TryCreate("Pomona", "QLD", out var location, out var error), error);

        var staging = Path.Combine(Path.GetTempPath(), "bom-browser-tests", Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture.CaptureAsync(location!, staging, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, browser.ResetRequests);
    }

    [Fact]
    public async Task Empty_debug_folder_is_deleted_through_owned_path_helper()
    {
        var cacheRoot = CreateTempRoot();
        try
        {
            var debugService = new TempDebugService(cacheRoot);
            var capture = CreateCapture(new FailingBrowserService(), debugService, cacheRoot);
            var location = LocationId.Create("Pomona", "QLD");

            var exception = await Assert.ThrowsAsync<RadarCaptureException>(
                () => capture.CaptureAsync(
                    location,
                    Path.Combine(cacheRoot, ".staging", "capture"),
                    CancellationToken.None));

            Assert.Equal("BROWSER_ERROR", exception.ErrorCode);
            var debugRoot = Path.Combine(cacheRoot, "debug");
            Assert.True(Directory.Exists(debugRoot));
            Assert.Empty(Directory.EnumerateDirectories(debugRoot));
        }
        finally
        {
            TryDeleteTempRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task Non_empty_debug_folder_is_not_deleted()
    {
        var cacheRoot = CreateTempRoot();
        try
        {
            var debugService = new TempDebugService(cacheRoot, writeMarker: true);
            var capture = CreateCapture(new FailingBrowserService(), debugService, cacheRoot);
            var location = LocationId.Create("Pomona", "QLD");

            await Assert.ThrowsAsync<RadarCaptureException>(
                () => capture.CaptureAsync(
                    location,
                    Path.Combine(cacheRoot, ".staging", "capture"),
                    CancellationToken.None));

            var debugRoot = Path.Combine(cacheRoot, "debug");
            var requestFolder = Assert.Single(Directory.EnumerateDirectories(debugRoot));
            Assert.True(File.Exists(Path.Combine(requestFolder, "marker.txt")));
        }
        finally
        {
            TryDeleteTempRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task Scrub_read_honors_a_pre_cancelled_deadline_before_touching_the_page()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var parser = CreateScrubParser();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parser.ReadUtcAsync(
                null!,
                null!,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Scrub_wait_loop_honors_a_pre_cancelled_deadline_before_touching_the_page()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var parser = CreateScrubParser();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parser.WaitForDisplayLabelChangeAsync(
                null!,
                null!,
                DateTime.UtcNow,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Scrub_read_propagates_playwright_cancellation_when_deadline_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = CreateScrubParser();
        var selector = new CancellingSelectorService(
            cancellation,
            new PlaywrightException("Target page, context or browser has been closed"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parser.ReadUtcAsync(
                null!,
                selector,
                cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Scrub_wait_loop_propagates_cancellation_after_a_read()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = CreateScrubParser();
        var selector = new CancellingSelectorService(
            cancellation,
            new InvalidOperationException("label read failed"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parser.WaitForDisplayLabelChangeAsync(
                null!,
                selector,
                DateTime.UtcNow,
                maxWaitMs: 5000,
                cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
    }

    private static ScrubDisplayTimestampParser CreateScrubParser()
    {
        return new ScrubDisplayTimestampParser(
            new ConfigurationBuilder().Build(),
            NullLogger<ScrubDisplayTimestampParser>.Instance);
    }

    private static RadarCaptureService CreateCapture(
        IBrowserService browser,
        IDebugService debugService,
        string cacheRoot)
    {
        return new RadarCaptureService(
            NullLogger<RadarCaptureService>.Instance,
            browser,
            new UnusedScrapingService(),
            debugService,
            new FixedCacheService(cacheRoot));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-browser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private static BrowserLeaseCoordinator<FakeBrowser, FakeContext> CreateCoordinator(
        Func<FakeContext, Task>? closeContext = null,
        Func<FakeBrowser, Task>? closeBrowser = null,
        Action<string, Exception?>? observe = null,
        TimeSpan? cleanupTimeout = null)
    {
        return new BrowserLeaseCoordinator<FakeBrowser, FakeContext>(
            cleanupTimeout ?? TimeSpan.FromSeconds(5),
            closeContext ?? (_ => Task.CompletedTask),
            closeBrowser ?? (_ => Task.CompletedTask),
            observe ?? ((_, _) => { }));
    }

    private sealed class FakeResource
    {
        public FakeResource(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class FakeContext
    {
        private int _closeCount;

        public FakeContext(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int CloseCount => Volatile.Read(ref _closeCount);

        public void RecordClose() => Interlocked.Increment(ref _closeCount);
    }

    private sealed class FakeBrowser
    {
        private int _closeCount;

        public FakeBrowser(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int CloseCount => Volatile.Read(ref _closeCount);

        public void RecordClose() => Interlocked.Increment(ref _closeCount);
    }

    /// <summary>
    /// Fails context creation only after the deadline token has been cancelled,
    /// standing in for Playwright's "target closed" failure during cancellation.
    /// </summary>
    private sealed class CancellingBrowserService : IBrowserService
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingBrowserService(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int ResetRequests { get; private set; }

        public bool IsBrowserConnected => false;

        public Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default)
        {
            _cancellation.Cancel();
            throw new PlaywrightException("Target page, context or browser has been closed");
        }

        public Task<bool> ReleaseContextAsync(
            IBrowserContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
            IBrowserContext context,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            ResetRequests++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Cancels the caller's deadline at the selector boundary. Throwing a
    /// Playwright failure models a target/context close during the read; throwing
    /// an ordinary failure models a recoverable read miss before the wait loop's
    /// post-read deadline check.
    /// </summary>
    private sealed class CancellingSelectorService : ISelectorService
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Exception _failure;

        public CancellingSelectorService(CancellationTokenSource cancellation, Exception failure)
        {
            _cancellation = cancellation;
            _failure = failure;
        }

        public Task<ILocator?> FindElementAsync(
            IPage page,
            SelectorConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ILocator?>(new NotSupportedException());

        public ILocator GetLocator(IPage page, SelectorConfig config)
        {
            _cancellation.Cancel();
            throw _failure;
        }
    }

    private sealed class UnusedScrapingService : IScrapingService
    {
        public Task<RadarResponse> ScrapeRadarScreenshotAsync(
            string suburb,
            string state,
            string cacheFolderPath,
            string debugFolder,
            IPage page,
            List<(string type, string text, DateTime timestamp)> consoleMessages,
            List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

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

    /// <summary>
    /// Creates a request folder under the real debug root shape so the production
    /// cleanup path can resolve it through FilePathHelper.
    /// </summary>
    private sealed class TempDebugService : IDebugService
    {
        private readonly string _cacheRoot;
        private readonly bool _writeMarker;

        public TempDebugService(string cacheRoot, bool writeMarker = false)
        {
            _cacheRoot = cacheRoot;
            _writeMarker = writeMarker;
        }

        public bool IsEnabled => true;

        public string CreateRequestFolder(string requestId)
        {
            var folder = Path.Combine(_cacheRoot, "debug", requestId);
            Directory.CreateDirectory(folder);
            if (_writeMarker)
            {
                File.WriteAllText(Path.Combine(folder, "marker.txt"), "marker");
            }

            return folder;
        }

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

    private sealed class FailingBrowserService : IBrowserService
    {
        public bool IsBrowserConnected => false;

        public Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IBrowserContext>(
                new PlaywrightException("Context creation failed for cleanup test"));

        public Task<bool> ReleaseContextAsync(
            IBrowserContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
            IBrowserContext context,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedCacheService : ICacheService
    {
        private readonly string _root;

        public FixedCacheService(string root)
        {
            _root = root;
        }

        public string GetCacheDirectory() => _root;

        public bool IsCacheValid(LastUpdatedInfo? metadata) => false;

        public int CleanupIncompleteCacheFolders() => 0;

        public Task<CacheSnapshot?> GetLatestSnapshotAsync(
            LocationId location,
            CachedDataType dataType,
            CancellationToken cancellationToken = default) => Task.FromResult<CacheSnapshot?>(null);

        public Task<IReadOnlyList<CacheSnapshot>> GetSnapshotsAsync(
            LocationId location,
            CachedDataType dataType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CacheSnapshot>>(Array.Empty<CacheSnapshot>());

        public Task<CacheSnapshot?> GetSnapshotAsync(
            LocationId location,
            string cacheFolderName,
            CachedDataType dataType,
            CancellationToken cancellationToken = default) => Task.FromResult<CacheSnapshot?>(null);

        public Task<CacheSnapshot?> GetSnapshotContainingFrameAsync(
            LocationId location,
            string cacheFolderName,
            int frameIndex,
            CachedDataType dataType,
            CancellationToken cancellationToken = default) => Task.FromResult<CacheSnapshot?>(null);

        public Task<IReadOnlyList<CacheSnapshot>> GetAllSnapshotsAsync(
            CachedDataType dataType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CacheSnapshot>>(Array.Empty<CacheSnapshot>());

        public IReadOnlyList<CacheInventoryEntry> GetCacheInventory() =>
            Array.Empty<CacheInventoryEntry>();

        public string CreateStagingDirectory(LocationId location, DateTime capturedAtUtc) =>
            throw new NotSupportedException();

        public Task SaveMetadataAsync(
            string cacheFolderPath,
            LastUpdatedInfo metadata,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveFramesMetadataAsync(
            string cacheFolderPath,
            CachedDataType dataType,
            List<RadarFrame> frames,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CacheSnapshot> PublishSnapshotAsync(
            LocationId location,
            string stagingDirectory,
            CachedDataType dataType,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteCachedLocationAsync(
            LocationId location,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> DeleteSnapshotFolderAsync(
            string folderName,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public void RecordUpdateProgressByFolder(
            string cacheFolderPath,
            CacheUpdatePhase phase,
            int? currentFrame = null,
            int? totalFrames = null)
        {
        }

        public int GetEstimatedRemainingSeconds(string locationKey) => 0;

        public void RecordStepCompletion(string stepName, double durationSeconds)
        {
        }

        public double GetAverageStepDuration(string stepName) => 0;

        public Dictionary<string, double> GetStepMetrics() => new();

        public double GetAverageTotalDuration() => 0;
    }
}
