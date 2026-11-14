using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public class DebugService : IDebugService
{
    private readonly ILogger<DebugService> _logger;
    private readonly bool _enabled;
    private readonly string _cacheRoot;
    private readonly string _debugDirectory;
    private readonly int _waitMs;

    public DebugService(ILogger<DebugService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var waitMsConfig = configuration.GetValue<int?>("Debug:WaitMs");
        if (!waitMsConfig.HasValue)
        {
            throw new InvalidOperationException("Debug:WaitMs configuration is required. Set it in appsettings.json or via DEBUG__WAITMS environment variable.");
        }

        _waitMs = waitMsConfig.Value;
        _enabled = configuration.GetValue<bool>("Debug:Enabled", false);

        _cacheRoot = Path.GetFullPath(FilePathHelper.GetCacheDirectory(configuration));
        Directory.CreateDirectory(_cacheRoot);

        if (_enabled)
        {
            if (!FilePathHelper.TryEnsureDirectChildDirectory(
                    _cacheRoot,
                    FilePathHelper.DebugDirectoryName,
                    out var debugRoot,
                    out var error))
            {
                throw new InvalidOperationException($"Debug directory is unsafe: {error}");
            }

            _debugDirectory = debugRoot!.FullPath;
            _logger.LogInformation("Debug mode is ENABLED. Debug files will be saved to: {DebugDirectory}", _debugDirectory);
        }
        else
        {
            _debugDirectory = FilePathHelper.GetDebugRoot(_cacheRoot);
            _logger.LogInformation("Debug mode is DISABLED");
        }
    }

    public bool IsEnabled => _enabled;

    public string CreateRequestFolder(string requestId)
    {
        if (!_enabled)
        {
            return string.Empty;
        }

        if (!IsSimpleName(requestId))
        {
            _logger.LogWarning("Rejected debug request folder name");
            return string.Empty;
        }

        if (!TryEnsureDebugDirectory(
                $"{FilePathHelper.DebugDirectoryName}/{requestId}",
                out var requestFolder,
                out var error))
        {
            _logger.LogWarning("Could not create debug request folder: {Error}", error);
            return string.Empty;
        }

        _logger.LogDebug("Created debug folder for request: {RequestId} at {Path}", requestId, requestFolder);
        return requestFolder!;
    }

    public async Task SaveStepDebugAsync(
        string requestFolder,
        int stepNumber,
        string stepName,
        IPage page,
        List<(string type, string text, DateTime timestamp)>? consoleMessages = null,
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || string.IsNullOrEmpty(requestFolder))
        {
            return;
        }

        if (!TryGetRequestFolder(requestFolder, out var requestId, out _) ||
            !IsSimpleName($"step_{stepNumber:D2}_{stepName}"))
        {
            _logger.LogWarning("Rejected debug step folder");
            return;
        }

        var stepFolderName = $"step_{stepNumber:D2}_{stepName}";
        if (!TryEnsureDebugDirectory(
                $"{FilePathHelper.DebugDirectoryName}/{requestId}/{stepFolderName}",
                out _,
                out var stepError))
        {
            _logger.LogWarning("Could not create debug step folder: {Error}", stepError);
            return;
        }

        try
        {
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 });
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 5000 });
            }
            catch
            {
                // If network idle times out, at least wait for DOM.
            }

            await page.WaitForTimeoutAsync(_waitMs);

            if (!TryGetFile($"{requestId}/{stepFolderName}/screenshot.png", out var screenshotPath) ||
                !TryGetFile($"{requestId}/{stepFolderName}/page.html", out var htmlPath))
            {
                _logger.LogWarning("Rejected unsafe debug step file path");
                return;
            }

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath!,
                FullPage = true
            });

            var htmlContent = await page.ContentAsync();
            await File.WriteAllTextAsync(htmlPath!, htmlContent, cancellationToken);

            if (consoleMessages is { Count: > 0 })
            {
                if (!TryGetFile($"{requestId}/{stepFolderName}/console.log", out var consolePath))
                {
                    return;
                }

                var consoleSnapshot = consoleMessages.ToList();
                var consoleText = string.Join("\n", consoleSnapshot.Select(msg => $"[{msg.timestamp:HH:mm:ss.fff}] [{msg.type}] {msg.text}"));
                await File.WriteAllTextAsync(consolePath!, consoleText, cancellationToken);
            }

            if (networkRequests is { Count: > 0 })
            {
                if (!TryGetFile($"{requestId}/{stepFolderName}/network.log", out var networkPath))
                {
                    return;
                }

                var networkSnapshot = networkRequests.ToList();
                var networkText = string.Join("\n", networkSnapshot.Select(req =>
                    $"[{req.timestamp:HH:mm:ss.fff}] {req.method} {req.url} -> {req.status?.ToString() ?? "pending"} ({req.resourceType})"));
                await File.WriteAllTextAsync(networkPath!, networkText, cancellationToken);
            }

            _logger.LogDebug("Saved debug files for step {StepNumber} ({StepName}) to {Path}", stepNumber, stepName, stepFolderName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug files for step {StepNumber} ({StepName})", stepNumber, stepName);
        }
    }

    public async Task SaveErrorDebugAsync(
        string requestFolder,
        string errorMessage,
        IPage? page = null,
        List<(string type, string text, DateTime timestamp)>? consoleMessages = null,
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || string.IsNullOrEmpty(requestFolder))
        {
            return;
        }

        if (!TryGetRequestFolder(requestFolder, out var requestId, out _))
        {
            _logger.LogWarning("Rejected unsafe debug error folder");
            return;
        }

        if (!TryEnsureDebugDirectory(
                $"{FilePathHelper.DebugDirectoryName}/{requestId}/error",
                out _,
                out var errorFolderError))
        {
            _logger.LogWarning("Could not create debug error folder: {Error}", errorFolderError);
            return;
        }

        try
        {
            if (!TryGetFile($"{requestId}/error/error.txt", out var errorPath))
            {
                return;
            }

            await File.WriteAllTextAsync(errorPath!, errorMessage, cancellationToken);

            if (page != null)
            {
                if (!TryGetFile($"{requestId}/error/screenshot.png", out var screenshotPath) ||
                    !TryGetFile($"{requestId}/error/page.html", out var htmlPath))
                {
                    return;
                }

                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath!,
                    FullPage = true
                });

                var htmlContent = await page.ContentAsync();
                await File.WriteAllTextAsync(htmlPath!, htmlContent, cancellationToken);
            }

            if (consoleMessages is { Count: > 0 })
            {
                if (!TryGetFile($"{requestId}/error/console.log", out var consolePath))
                {
                    return;
                }

                var consoleSnapshot = consoleMessages.ToList();
                var consoleText = string.Join("\n", consoleSnapshot.Select(msg => $"[{msg.timestamp:HH:mm:ss.fff}] [{msg.type}] {msg.text}"));
                await File.WriteAllTextAsync(consolePath!, consoleText, cancellationToken);
            }

            if (networkRequests is { Count: > 0 })
            {
                if (!TryGetFile($"{requestId}/error/network.log", out var networkPath))
                {
                    return;
                }

                var networkSnapshot = networkRequests.ToList();
                var networkText = string.Join("\n", networkSnapshot.Select(req =>
                    $"[{req.timestamp:HH:mm:ss.fff}] {req.method} {req.url} -> {req.status?.ToString() ?? "pending"} ({req.resourceType})"));
                await File.WriteAllTextAsync(networkPath!, networkText, cancellationToken);
            }

            _logger.LogDebug("Saved error debug files for request {RequestId}", requestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save error debug files");
        }
    }

    private bool TryGetRequestFolder(string requestFolder, out string requestId, out string? requestPath)
    {
        requestId = Path.GetFileName(requestFolder.TrimEnd(Path.DirectorySeparatorChar));
        requestPath = null;

        if (!IsSimpleName(requestId))
        {
            return false;
        }

        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheRoot,
                $"{FilePathHelper.DebugDirectoryName}/{requestId}",
                allowMissingLeaf: false,
                out requestPath,
                out _) ||
            !Directory.Exists(requestPath))
        {
            return false;
        }

        return true;
    }

    private bool TryEnsureDebugDirectory(
        string relativePath,
        out string? path,
        out string? error)
    {
        path = null;
        error = null;

        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheRoot,
                relativePath,
                allowMissingLeaf: true,
                out path,
                out error))
        {
            return false;
        }

        if (File.Exists(path) && !Directory.Exists(path))
        {
            error = "A file exists where a debug directory is required";
            return false;
        }

        try
        {
            Directory.CreateDirectory(path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not create debug directory: {ex.Message}";
            return false;
        }

        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheRoot,
                relativePath,
                allowMissingLeaf: false,
                out path,
                out error) ||
            !Directory.Exists(path))
        {
            return false;
        }

        return true;
    }

    private bool TryGetFile(string relativePath, out string? path) =>
        FilePathHelper.TryResolveOwnedPath(
            _cacheRoot,
            $"{FilePathHelper.DebugDirectoryName}/{relativePath}",
            allowMissingLeaf: true,
            out path,
            out _);

    private static bool IsSimpleName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() &&
        value != "." &&
        value != ".." &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Contains('*') &&
        !value.Contains('?') &&
        !Path.IsPathRooted(value) &&
        !value.Any(char.IsControl);
}
