using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

/// <summary>
/// Persists useful per-location operational state outside the capture-folder
/// namespace. Cache cleanup never scans this directory. Every operation is
/// anchored at the configured cache root so replacing .service or locations
/// after construction fails closed.
/// </summary>
public sealed class LocationStateStore : ILocationStateStore
{
    private const int MaxStateBytes = 64 * 1024;
    private const string LocationsDirectoryName = "locations";

    private readonly ILogger<LocationStateStore> _logger;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private string StateDirectoryRelative =>
        $"{FilePathHelper.ServiceStateDirectoryName}/{LocationsDirectoryName}";

    public LocationStateStore(ILogger<LocationStateStore> logger, IConfiguration configuration)
    {
        _logger = logger;

        _cacheRoot = Path.GetFullPath(FilePathHelper.GetCacheDirectory(configuration));
        Directory.CreateDirectory(_cacheRoot);

        if (!FilePathHelper.TryEnsureDirectChildDirectory(
                _cacheRoot,
                FilePathHelper.ServiceStateDirectoryName,
                out _,
                out var serviceError))
        {
            throw new InvalidOperationException($"Service state directory is unsafe: {serviceError}");
        }

        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheRoot,
                StateDirectoryRelative,
                allowMissingLeaf: true,
                out var locationsPath,
                out var locationsError))
        {
            throw new InvalidOperationException($"Location state directory is unsafe: {locationsError}");
        }

        try
        {
            Directory.CreateDirectory(locationsPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException($"Location state directory could not be created: {ex.Message}", ex);
        }

        if (!FilePathHelper.TryResolveOwnedPath(
                _cacheRoot,
                StateDirectoryRelative,
                allowMissingLeaf: false,
                out var validatedLocations,
                out var validationError) ||
            !Directory.Exists(validatedLocations))
        {
            throw new InvalidOperationException($"Location state directory is unsafe: {validationError}");
        }
    }

    public async Task<LocationRuntimeState?> GetAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = GetRelativePath(location);
        if (!TryResolve(relativePath, allowMissingLeaf: true, out var path, out var pathError))
        {
            _logger.LogWarning("Refusing unsafe location state path for {Location}: {Error}", location, pathError);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        if (!CacheHelper.TryReadBoundedJson<LocationRuntimeState>(
                path!,
                MaxStateBytes,
                out var state,
                out var readError) ||
            state is null)
        {
            _logger.LogWarning("Failed to read location state for {Location}: {Error}", location, readError);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!LocationId.TryCreate(state.Suburb, state.State, out var storedLocation, out _) ||
            !storedLocation!.Equals(location))
        {
            _logger.LogWarning("Location state file does not match the requested location: {Path}", path);
            return null;
        }

        return state;
    }

    public async Task<bool> SaveAsync(LocationRuntimeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!LocationId.TryCreate(state.Suburb, state.State, out var location, out var locationError))
        {
            _logger.LogWarning(
                "Refusing to persist invalid location state for {Suburb}, {State}: {Error}",
                state.Suburb,
                state.State,
                locationError);
            return false;
        }

        var snapshot = CopyState(state);
        var relativePath = GetRelativePath(location!);
        var tempRelativePath = relativePath + ".tmp";

        await _writeLock.WaitAsync(cancellationToken);
        string? tempPath = null;
        string? path = null;
        string? pathError = null;
        try
        {
            if (!TryResolve(relativePath, allowMissingLeaf: true, out path, out pathError) ||
                !TryResolve(tempRelativePath, allowMissingLeaf: true, out tempPath, out pathError))
            {
                _logger.LogWarning(
                    "Failed to persist location state for {Location}: {Error}",
                    location,
                    pathError);
                return false;
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);

            await using (var stream = new FileStream(
                             tempPath!,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            // Revalidate destination and temp from the cache root immediately
            // before the commit. This narrows, but cannot eliminate, TOCTOU.
            if (!TryResolve(relativePath, allowMissingLeaf: true, out path, out pathError) ||
                !TryResolve(tempRelativePath, allowMissingLeaf: true, out tempPath, out pathError))
            {
                _logger.LogWarning(
                    "Refusing location state commit for {Location}: {Error}",
                    location,
                    pathError);
                return false;
            }

            File.Move(tempPath!, path!, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Failed to persist location state for {Location}", location);
            return false;
        }
        finally
        {
            try
            {
                if (TryResolve(tempRelativePath, allowMissingLeaf: true, out var safeTempPath, out _) &&
                    File.Exists(safeTempPath) &&
                    !FilePathHelper.IsSymlinkOrReparsePoint(safeTempPath))
                {
                    File.Delete(safeTempPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; never follow a replaced ancestor.
            }

            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<LocationRuntimeState>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LocationRuntimeState>();
        cancellationToken.ThrowIfCancellationRequested();

        // Validate the directory from the cache root before enumerating anything.
        if (!TryResolve(StateDirectoryRelative, allowMissingLeaf: false, out var stateDirectory, out var stateDirectoryError) ||
            !Directory.Exists(stateDirectory))
        {
            _logger.LogWarning(
                "Refusing to enumerate an unsafe or missing location state directory: {Error}",
                stateDirectoryError);
            return result;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(stateDirectory!, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate location state directory");
            return result;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);
            var relativePath = $"{StateDirectoryRelative}/{fileName}";
            if (!TryResolve(relativePath, allowMissingLeaf: false, out var path, out var pathError))
            {
                _logger.LogWarning("Ignoring unsafe location state file {File}: {Error}", fileName, pathError);
                continue;
            }

            if (!CacheHelper.TryReadBoundedJson<LocationRuntimeState>(
                    path!,
                    MaxStateBytes,
                    out var state,
                    out var readError) ||
                state is null)
            {
                _logger.LogWarning("Failed to read location state file {File}: {Error}", fileName, readError);
                continue;
            }

            if (!LocationId.TryCreate(state.Suburb, state.State, out var storedLocation, out _) ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(fileName),
                    GetHash(storedLocation!),
                    StringComparison.Ordinal))
            {
                _logger.LogWarning("Ignoring location state file with invalid identity: {File}", fileName);
                continue;
            }

            result.Add(state);
        }

        return result;
    }

    private string GetRelativePath(LocationId location) =>
        $"{StateDirectoryRelative}/{GetHash(location)}.json";

    private bool TryResolve(
        string relativePath,
        bool allowMissingLeaf,
        out string? path,
        out string? error) =>
        FilePathHelper.TryResolveOwnedPath(_cacheRoot, relativePath, allowMissingLeaf, out path, out error);

    private static string GetHash(LocationId location)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location.Key)))
            .ToLowerInvariant()[..32];
        return hash;
    }

    private static LocationRuntimeState CopyState(LocationRuntimeState state) => new()
    {
        Suburb = state.Suburb,
        State = state.State,
        LastAttemptAt = state.LastAttemptAt,
        LastSuccessfulCaptureAt = state.LastSuccessfulCaptureAt,
        LatestObservationAt = state.LatestObservationAt,
        NextRetryAt = state.NextRetryAt,
        UpdateFailed = state.UpdateFailed,
        ErrorCode = state.ErrorCode,
        Error = state.Error,
        ConsecutiveFailures = state.ConsecutiveFailures,
        UpdatedAt = state.UpdatedAt
    };
}
