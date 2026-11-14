using System.Globalization;
using System.Text;
using BomLocalService.Models;

namespace BomLocalService.Utilities;

public sealed record CacheFolderResolution(string FolderName, string FullPath);

public static class FilePathHelper
{
    public const string StagingDirectoryName = ".staging";
    public const string ServiceStateDirectoryName = ".service";
    public const string DebugDirectoryName = "debug";

    private const int MaxChildNameBytes = 255;

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

    public static string GetDebugRoot(string cacheDirectory) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory)), DebugDirectoryName);

    /// <summary>
    /// Creates a staging folder name for a capture. Staging folders live on the
    /// same filesystem as the cache so publication can be a single rename.
    /// </summary>
    public static string GetStagingFolderPath(string cacheDirectory, LocationId location, DateTime capturedAtUtc, string uniqueSuffix)
    {
        if (!LocationHelper.TryGetStagingFolderName(location, capturedAtUtc, uniqueSuffix, out var name, out var error))
        {
            throw new ArgumentException(error, nameof(location));
        }

        return Path.Combine(GetStagingRoot(cacheDirectory), name!);
    }

    public static bool TryGetStagingFolderPath(
        string cacheDirectory,
        LocationId location,
        DateTime capturedAtUtc,
        string uniqueSuffix,
        out string? path,
        out string? error)
    {
        path = null;
        if (!LocationHelper.TryGetStagingFolderName(location, capturedAtUtc, uniqueSuffix, out var name, out error))
        {
            return false;
        }

        path = Path.Combine(GetStagingRoot(cacheDirectory), name!);
        return true;
    }

    public static string GetFinalFolderName(LocationId location, DateTime capturedAtUtc)
    {
        if (!LocationHelper.TryGetCacheFolderBaseName(location, capturedAtUtc, out var name, out var error))
        {
            throw new ArgumentException(error, nameof(location));
        }

        return name!;
    }

    public static bool TryGetFinalFolderName(
        LocationId location,
        DateTime capturedAtUtc,
        out string? folderName,
        out string? error) =>
        LocationHelper.TryGetCacheFolderBaseName(location, capturedAtUtc, out folderName, out error);

    /// <summary>
    /// Resolves a '/'-separated relative path beneath a trusted cache root. Every
    /// existing component, including the root, must be a real file or directory;
    /// symlinks and reparse points are rejected before any read/write/delete.
    /// The leaf may be absent only when <paramref name="allowMissingLeaf"/> is true.
    /// </summary>
    public static bool TryResolveOwnedPath(
        string cacheDirectory,
        string relativePath,
        bool allowMissingLeaf,
        out string? fullPath,
        out string? error)
    {
        fullPath = null;
        error = null;

        if (!TryGetTrustedRoot(cacheDirectory, out var root, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath != relativePath.Trim() ||
            relativePath.Contains('\\') ||
            relativePath.Contains(':') ||
            relativePath.StartsWith('/') ||
            Path.IsPathRooted(relativePath))
        {
            error = "Relative path must be a simple '/'-separated child path";
            return false;
        }

        var segments = relativePath.Split('/');
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (!IsValidChildName(segment, out error))
            {
                return false;
            }

            var next = Path.Combine(current, segment);
            if (!IsUnderRoot(root, next))
            {
                error = "Path escaped the configured cache root";
                return false;
            }

            if (!TryGetEntry(next, out var exists, out var attributes, out error))
            {
                return false;
            }

            if (exists)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Path must not be or traverse a symbolic link or reparse point";
                    return false;
                }

                if (index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                {
                    error = "A path component is not a directory";
                    return false;
                }
            }
            else if (!allowMissingLeaf || index != segments.Length - 1)
            {
                error = "Path component does not exist";
                return false;
            }

            current = next;
        }

        fullPath = current;
        return true;
    }

    /// <summary>
    /// Backward-compatible direct-child resolver for existing callers.
    /// </summary>
    public static bool TryResolveDirectChild(
        string cacheDirectory,
        string? folderName,
        out CacheFolderResolution? resolution,
        out string? error)
    {
        resolution = null;

        if (string.IsNullOrWhiteSpace(folderName))
        {
            error = "Cache folder name is required";
            return false;
        }

        if (!IsValidChildName(folderName, out error))
        {
            return false;
        }

        if (!TryResolveOwnedPath(cacheDirectory, folderName, allowMissingLeaf: false, out var fullPath, out error))
        {
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = "Cache folder does not exist";
            return false;
        }

        resolution = new CacheFolderResolution(folderName, fullPath!);
        return true;
    }

    /// <summary>
    /// Backward-compatible child resolver. The leaf may be absent so callers can
    /// validate before creating or overwriting; parents and symlink components are
    /// always checked.
    /// </summary>
    public static bool TryResolveOwnedChild(
        string cacheDirectory,
        string parentFolderPath,
        string childName,
        out string? childPath,
        out string? error)
    {
        childPath = null;

        if (!IsValidChildName(childName, out error))
        {
            return false;
        }

        if (!TryGetTrustedRoot(cacheDirectory, out var root, out error))
        {
            return false;
        }

        string parent;
        try
        {
            parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentFolderPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Parent path is not valid";
            return false;
        }

        if (!IsUnderRoot(root, parent))
        {
            error = "Parent path escaped the configured cache root";
            return false;
        }

        var relativeParent = Path.GetRelativePath(root, parent);
        var relativePath = relativeParent == "."
            ? childName
            : $"{relativeParent.Replace(Path.DirectorySeparatorChar, '/')}/{childName}";

        return TryResolveOwnedPath(cacheDirectory, relativePath, allowMissingLeaf: true, out childPath, out error);
    }

    /// <summary>
    /// Ensures a direct child directory exists and is not a symlink/reparse point.
    /// </summary>
    public static bool TryEnsureDirectChildDirectory(
        string cacheDirectory,
        string childName,
        out CacheFolderResolution? resolution,
        out string? error)
    {
        resolution = null;

        if (!IsValidChildName(childName, out error))
        {
            return false;
        }

        if (!TryResolveOwnedPath(cacheDirectory, childName, allowMissingLeaf: true, out var fullPath, out error))
        {
            return false;
        }

        try
        {
            if (File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                error = "A file already exists where a directory is required";
                return false;
            }

            Directory.CreateDirectory(fullPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not create directory: {ex.Message}";
            return false;
        }

        if (!TryResolveOwnedPath(cacheDirectory, childName, allowMissingLeaf: false, out fullPath, out error) ||
            !Directory.Exists(fullPath))
        {
            return false;
        }

        resolution = new CacheFolderResolution(childName, fullPath!);
        return true;
    }

    /// <summary>
    /// Deletes an owned file or tree. The tree is pre-scanned; if any descendant is
    /// a symlink/reparse point, nothing is deleted.
    /// </summary>
    public static bool TryDeleteOwnedTree(
        string cacheDirectory,
        string relativePath,
        out string? error)
    {
        if (!TryResolveOwnedPath(cacheDirectory, relativePath, allowMissingLeaf: false, out var fullPath, out error))
        {
            return false;
        }

        try
        {
            if (File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                File.Delete(fullPath!);
                return true;
            }

            if (!Directory.Exists(fullPath))
            {
                error = "Path is not a file or directory";
                return false;
            }

            var files = new List<string>();
            var directories = new List<string>();
            if (!TryCollectTree(fullPath!, files, directories, out error))
            {
                return false;
            }

            foreach (var file in files)
            {
                File.Delete(file);
            }

            for (var index = directories.Count - 1; index >= 0; index--)
            {
                Directory.Delete(directories[index], recursive: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not delete path: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates the configured cache root itself before any enumeration or
    /// operation. Returns the normalized root on success.
    /// </summary>
    public static bool TryValidateCacheRoot(
        string cacheDirectory,
        out string? root,
        out string? error)
    {
        if (TryGetTrustedRoot(cacheDirectory, out var trustedRoot, out error))
        {
            root = trustedRoot;
            return true;
        }

        root = null;
        return false;
    }

    public static bool IsUnderRoot(string cacheDirectory, string candidatePath)
    {
        string root;
        string candidate;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    public static bool IsSymlinkOrReparsePoint(string path)
    {
        // Access failures are treated as unsafe rather than as "not a link".
        if (!TryGetEntry(path, out var exists, out var attributes, out _))
        {
            return true;
        }

        return exists && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    public static bool IsReservedDirectoryName(string name) =>
        name.StartsWith(".", StringComparison.Ordinal) ||
        string.Equals(name, DebugDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the cache folder path for a location and timestamp.
    /// </summary>
    public static string GetCacheFolderPath(string cacheDirectory, LocationId location, string timestamp)
    {
        if (DateTime.TryParseExact(
                timestamp,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return Path.Combine(cacheDirectory, GetFinalFolderName(location, parsed));
        }

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
    /// Gets the cache folder pattern for finding existing folders. Only used for
    /// non-security-sensitive discovery; ownership checks must still validate each
    /// result.
    /// </summary>
    public static string GetCacheFolderPattern(string suburb, string state)
    {
        return $"{LocationHelper.GetCacheFolderPrefix(LocationId.Create(suburb, state))}_*";
    }

    public static string GetCacheLockFilePath(string cacheFolderPath) =>
        Path.Combine(cacheFolderPath, ".writing");

    private static bool TryGetTrustedRoot(string cacheDirectory, out string root, out string? error)
    {
        root = string.Empty;
        error = null;

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Cache root is not a valid path";
            return false;
        }

        if (!TryGetEntry(root, out var exists, out var attributes, out error))
        {
            return false;
        }

        if (!exists)
        {
            error = "Cache root does not exist";
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            error = "Cache root must not be a symbolic link or reparse point";
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            error = "Cache root is not a directory";
            return false;
        }

        return true;
    }

    private static bool IsValidChildName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required";
            return false;
        }

        if (name.Length > MaxChildNameBytes ||
            Encoding.UTF8.GetByteCount(name) > MaxChildNameBytes)
        {
            error = "Name is too long";
            return false;
        }

        if (name != name.Trim())
        {
            error = "Name must not have leading or trailing whitespace";
            return false;
        }

        if (name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Contains(':') ||
            name.Contains('*') ||
            name.Contains('?') ||
            Path.IsPathRooted(name) ||
            name.Any(char.IsControl))
        {
            error = "Name is not a valid direct child name";
            return false;
        }

        return true;
    }

    private static bool TryCollectTree(
        string root,
        List<string> files,
        List<string> directories,
        out string? error)
    {
        error = null;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            directories.Add(directory);

            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = $"Could not inspect directory: {ex.Message}";
                return false;
            }

            foreach (var entry in entries)
            {
                if (!TryGetEntry(entry, out var exists, out var attributes, out error))
                {
                    return false;
                }

                if (!exists)
                {
                    error = "Tree changed while it was being inspected";
                    return false;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Tree contains a symbolic link or reparse point; refusing to delete";
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return true;
    }

    private static bool TryGetEntry(
        string path,
        out bool exists,
        out FileAttributes attributes,
        out string? error)
    {
        exists = false;
        attributes = default;
        error = null;

        try
        {
            attributes = File.GetAttributes(path);
            exists = true;

            // File.GetAttributes may follow links on some platforms. An explicit
            // LinkTarget check makes the reparse decision reliable.
            try
            {
                if (new FileInfo(path).LinkTarget is not null)
                {
                    attributes |= FileAttributes.ReparsePoint;
                }
            }
            catch (IOException)
            {
                // The attribute result remains authoritative.
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Could not inspect path: {path}";
                return false;
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return TryGetDanglingLink(path, out exists, out attributes, out error);
        }
        catch (DirectoryNotFoundException)
        {
            return TryGetDanglingLink(path, out exists, out attributes, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not inspect path: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetDanglingLink(
        string path,
        out bool exists,
        out FileAttributes attributes,
        out string? error)
    {
        exists = false;
        attributes = default;
        error = null;

        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                exists = true;
                attributes = FileAttributes.ReparsePoint;
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not inspect path: {ex.Message}";
            return false;
        }
    }
}
