using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;

namespace BomLocalService.Services;

/// <summary>
/// Persists useful per-location operational state outside the capture-folder
/// namespace. Cache cleanup never scans this directory.
/// </summary>
public sealed class LocationStateStore : ILocationStateStore
{
    private readonly ILogger<LocationStateStore> _logger;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocationStateStore(ILogger<LocationStateStore> logger, IConfiguration configuration)
    {
        _logger = logger;
        _stateDirectory = Path.Combine(
            FilePathHelper.GetServiceStateRoot(FilePathHelper.GetCacheDirectory(configuration)),
            "locations");
        Directory.CreateDirectory(_stateDirectory);
    }

    public async Task<LocationRuntimeState?> GetAsync(LocationId location, CancellationToken cancellationToken = default)
    {
        var path = GetPath(location);
        if (!File.Exists(path) || FilePathHelper.IsSymlinkOrReparsePoint(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<LocationRuntimeState>(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read location state for {Location}", location);
            return null;
        }
    }

    public async Task SaveAsync(LocationRuntimeState state, CancellationToken cancellationToken = default)
    {
        if (!LocationId.TryCreate(state.Suburb, state.State, out var location, out _))
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            state.UpdatedAt = DateTime.UtcNow;
            var path = GetPath(location!);
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist location state for {Suburb}, {State}", state.Suburb, state.State);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<LocationRuntimeState>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LocationRuntimeState>();
        if (!Directory.Exists(_stateDirectory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_stateDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var state = JsonSerializer.Deserialize<LocationRuntimeState>(json);
                if (state != null)
                {
                    result.Add(state);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read location state file {File}", Path.GetFileName(file));
            }
        }

        return result;
    }

    private string GetPath(LocationId location)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location.Key)))
            .ToLowerInvariant()[..32];
        return Path.Combine(_stateDirectory, hash + ".json");
    }
}
