using BomLocalService.Models;

namespace BomLocalService.Utilities;

public sealed record CacheFolderResolution(string FolderName, string FullPath);

public static class FilePathHelper
{
    public const string StagingDirectoryName = ".staging";
    public const string ServiceStateDirectoryName = ".service";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Gets the cache directory path from configuration.
    /// </summary>
    public static string GetCacheDirectory(IConfiguration configuration)
    {
        return configuration.GetValue<string>("CacheDirectory")
            ?? throw new InvalidOperationException("CacheDirectory configuration is required. Set it in appsettings.json or via CACHEDIRECTORY environment variable.");
    }

    public static string GetStagingRoot(string cacheDirectory) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory)), StagingDirectoryName);

    public static string GetServiceStateRoot(string cacheDirectory) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory)), ServiceStateDirectoryName);

    /// <summary>
    /// Creates a staging folder name for a capture. Staging folders live on the
    /// same filesystem as the cache so publication can be a single rename.
    /// </summary>
    public static string GetStagingFolderPath(string cacheDirectory, LocationId location, DateTime capturedAtUtc, string uniqueSuffix)
    {
        var prefix = LocationHelper.GetCacheFolderPrefix(location);
        var name = $"{prefix}_{capturedAtUtc:yyyyMMdd_HHmmss}_{uniqueSuffix}";
        return Path.Combine(GetStagingRoot(cacheDirectory), name);
    }

    public static string GetFinalFolderName(LocationId location, DateTime capturedAtUtc) =>
        $"{LocationHelper.GetCacheFolderPrefix(location)}_{capturedAtUtc:yyyyMMdd_HHmmss}";

    /// <summary>
    /// Resolves a user-supplied cache folder name to a direct child of the cache root.
    /// Rejects separators, rooted paths, traversal segments, wildcards and control
    /// characters before touching the filesystem.
    /// </summary>
    public static bool TryResolveDirectChild(
        string cacheDirectory,
        string? folderName,
        out CacheFolderResolution? resolution,
        out string? error)
    {
        resolution = null;
        error = null;

        if (string.IsNullOrWhiteSpace(folderName))
        {
            error = "Cache folder name is required";
            return false;
        }

        if (folderName.Length > 255)
        {
            error = "Cache folder name is too long";
            return false;
        }

        if (folderName != folderName.Trim())
        {
            error = "Cache folder name must not have leading or trailing whitespace";
            return false;
        }

        if (folderName is "." or ".." ||
            folderName.Contains('/') ||
            folderName.Contains('\\') ||
            folderName.Contains(':') ||
            folderName.Contains('*') ||
            folderName.Contains('?') ||
            Path.IsPathRooted(folderName) ||
            folderName.Any(char.IsControl))
        {
            error = "Cache folder name is not a valid direct child name";
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
            candidate = Path.GetFullPath(Path.Combine(root, folderName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Cache folder name is not valid";
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(candidate), root, PathComparison))
        {
            error = "Cache folder must resolve directly beneath the cache root";
            return false;
        }

        if (!string.Equals(Path.GetFileName(candidate), folderName, StringComparison.Ordinal))
        {
            error = "Cache folder name does not match its resolved path";
            return false;
        }

        if (!Directory.Exists(candidate))
        {
            error = "Cache folder does not exist";
            return false;
        }

        if (IsSymlinkOrReparsePoint(candidate) || HasSymlinkComponent(root, candidate))
        {
            error = "Cache folder must not be or traverse a symbolic link";
            return false;
        }

        resolution = new CacheFolderResolution(folderName, candidate);
        return true;
    }

    /// <summary>
    /// Resolves a child of an owned cache folder (for example metadata.json or the
    /// radar directory) and rejects a symlink at any component below the cache root.
    /// </summary>
    public static bool TryResolveOwnedChild(
        string cacheDirectory,
        string parentFolderPath,
        string childName,
        out string? childPath,
        out string? error)
    {
        childPath = null;
        error = null;

        if (childName.Contains('/') || childName.Contains('\\') || childName is "." or "..")
        {
            error = "Child path must be a simple file or directory name";
            return false;
        }

        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentFolderPath));
        var fullChild = Path.GetFullPath(Path.Combine(fullParent, childName));
        if (!string.Equals(Path.GetDirectoryName(fullChild), fullParent, PathComparison))
        {
            error = "Child path escaped its parent";
            return false;
        }

        if (!IsUnderRoot(cacheDirectory, fullChild))
        {
            error = "Child path escaped the configured cache root";
            return false;
        }

        if (IsSymlinkOrReparsePoint(fullChild) || HasSymlinkComponent(cacheDirectory, fullChild))
        {
            error = "Child path must not be or traverse a symbolic link";
            return false;
        }

        childPath = fullChild;
        return true;
    }

    public static bool IsUnderRoot(string cacheDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    public static bool IsSymlinkOrReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasSymlinkComponent(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (!fullCandidate.StartsWith(fullRoot, PathComparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
        {
            return false;
        }

        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsSymlinkOrReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the cache folder path for a location and timestamp.
    /// Format: "{CacheDirectory}/{LocationKey}_{Timestamp}"
    /// </summary>
    public static string GetCacheFolderPath(string cacheDirectory, LocationId location, string timestamp)
    {
        var safeLocationKey = LocationHelper.GetCacheFolderPrefix(location);
        return Path.Combine(cacheDirectory, $"{safeLocationKey}_{timestamp}");
    }

    public static string GetCacheFolderPath(string cacheDirectory, string suburb, string state, string timestamp) =>
        GetCacheFolderPath(cacheDirectory, LocationId.Create(suburb, state), timestamp);

    /// <summary>
    /// Gets the data type subfolder path within a cache folder.
    /// </summary>
    public static string GetDataTypeFolderPath(string cacheFolderPath, CachedDataType dataType)
    {
        var dataTypeFolder = dataType.ToString().ToLowerInvariant();
        return Path.Combine(cacheFolderPath, dataTypeFolder);
    }

    /// <summary>
    /// Gets the frame file path within a data type subfolder.
    /// </summary>
    public static string GetFrameFilePath(string cacheFolderPath, CachedDataType dataType, int frameIndex)
    {
        var dataTypeFolder = GetDataTypeFolderPath(cacheFolderPath, dataType);
        return Path.Combine(dataTypeFolder, $"frame_{frameIndex}.png");
    }

    public static string GetMetadataFilePath(string cacheFolderPath) =>
        Path.Combine(cacheFolderPath, "metadata.json");

    public static string GetFramesMetadataFilePath(string cacheFolderPath, CachedDataType dataType)
    {
        var dataTypeFolder = GetDataTypeFolderPath(cacheFolderPath, dataType);
        return Path.Combine(dataTypeFolder, "frames.json");
    }

    public static string GetSnapshotCompleteMarkerPath(string cacheFolderPath) =>
        Path.Combine(cacheFolderPath, ".complete");

    /// <summary>
    /// Gets the cache folder pattern for finding existing folders.
    /// Only used for non-security-sensitive discovery; ownership checks must
    /// still validate each result.
    /// </summary>
    public static string GetCacheFolderPattern(string suburb, string state)
    {
        return $"{LocationHelper.GetCacheFolderPrefix(LocationId.Create(suburb, state))}_*";
    }

    public static string GetCacheLockFilePath(string cacheFolderPath) =>
        Path.Combine(cacheFolderPath, ".writing");
}
