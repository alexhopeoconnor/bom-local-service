using System.Globalization;
using System.Text;
using BomLocalService.Models;

namespace BomLocalService.Utilities;

public sealed record ParsedCacheFolder(
    string FolderName,
    LocationId Location,
    DateTime CacheTimestampUtc,
    bool IsStaging = false,
    string? UniqueSuffix = null);

public static class LocationHelper
{
    private const int MaxFolderNameBytes = 255;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Creates a display location key from suburb and state (e.g., "Pomona_QLD").
    /// </summary>
    public static string GetLocationKey(string suburb, string state)
    {
        return LocationId.TryCreate(suburb, state, out var location, out _)
            ? GetLocationKey(location!)
            : $"{suburb}_{state}";
    }

    public static string GetLocationKey(LocationId location) => $"{location.Suburb}_{location.State}";

    /// <summary>
    /// Sanitizes a filename using the historical on-disk convention. This is kept
    /// for legacy names and diagnostics only; new names use
    /// <see cref="TryGetCacheFolderBaseName"/> so lossy names are encoded exactly.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" ", "_")
            .Replace(",", "");
    }

    /// <summary>
    /// The historical prefix. Underscores here represent spaces; literal
    /// underscores and commas are therefore lossy and must use the v2 name.
    /// </summary>
    public static string GetCacheFolderPrefix(LocationId location) =>
        SanitizeFileName(GetLocationKey(location));

    /// <summary>
    /// True when the legacy prefix cannot round-trip this location exactly.
    /// </summary>
    public static bool RequiresEncodedCacheFolderName(LocationId location)
    {
        if (location.Suburb.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        var legacyName = $"{GetCacheFolderPrefix(location)}_20000101_000000";
        return !TryParseCacheFolderName(legacyName, out var parsed) ||
               !parsed!.Location.Equals(location);
    }

    /// <summary>
    /// Produces a final snapshot basename that round-trips to the exact
    /// <see cref="LocationId"/> without file I/O. Ordinary names keep the legacy
    /// shape; lossy comma/underscore names use the v2 reversible shape.
    /// </summary>
    public static bool TryGetCacheFolderBaseName(
        LocationId location,
        DateTime capturedAtUtc,
        out string? baseName,
        out string? error)
    {
        baseName = null;
        error = null;

        if (location is null)
        {
            error = "Location is required";
            return false;
        }

        var legacyTimestamp = capturedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var legacyName = $"{GetCacheFolderPrefix(location)}_{legacyTimestamp}";
        if (!location.Suburb.StartsWith(".", StringComparison.Ordinal) &&
            TryParseCacheFolderName(legacyName, out var legacyParsed) &&
            legacyParsed!.Location.Equals(location))
        {
            return ValidateFolderName(legacyName, out baseName, out error);
        }

        var encodedName = EncodeV2(location, capturedAtUtc);
        if (!ValidateFolderName(encodedName, out _, out error))
        {
            return false;
        }

        if (!TryParseCacheFolderName(encodedName, out var encodedParsed) ||
            !encodedParsed!.Location.Equals(location))
        {
            error = "Encoded cache folder name does not round-trip to the requested location";
            return false;
        }

        baseName = encodedName;
        return true;
    }

    /// <summary>
    /// Staging names add the existing unique hex suffix to the same base name so
    /// publication can verify exact ownership before a completion marker exists.
    /// </summary>
    public static bool TryGetStagingFolderName(
        LocationId location,
        DateTime capturedAtUtc,
        string uniqueSuffix,
        out string? folderName,
        out string? error)
    {
        folderName = null;
        error = null;

        if (string.IsNullOrEmpty(uniqueSuffix) ||
            uniqueSuffix.Length is < 8 or > 64 ||
            !uniqueSuffix.All(IsLowerHex))
        {
            error = "Staging suffix must be 8 to 64 lowercase hexadecimal characters";
            return false;
        }

        if (!TryGetCacheFolderBaseName(location, capturedAtUtc, out var baseName, out error))
        {
            return false;
        }

        var candidate = $"{baseName}_{uniqueSuffix}";
        if (!ValidateFolderName(candidate, out _, out error))
        {
            return false;
        }

        if (!TryParseCacheFolderName(candidate, allowStaging: true, out var parsed) ||
            !parsed!.IsStaging ||
            !parsed.Location.Equals(location))
        {
            error = "Staging folder name does not round-trip to the requested location";
            return false;
        }

        folderName = candidate;
        return true;
    }

    /// <summary>
    /// Parses a final cache basename. Staging names are rejected unless
    /// <paramref name="allowStaging"/> is true.
    /// </summary>
    public static bool TryParseCacheFolderName(string? folderName, out ParsedCacheFolder? parsed) =>
        TryParseCacheFolderName(folderName, allowStaging: false, out parsed);

    public static bool TryParseCacheFolderName(
        string? folderName,
        bool allowStaging,
        out ParsedCacheFolder? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var trimmed = folderName.Trim();
        if (trimmed != folderName ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            Path.IsPathRooted(trimmed))
        {
            return false;
        }

        var baseName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(baseName) || baseName is "." or "..")
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(baseName) > MaxFolderNameBytes)
        {
            return false;
        }

        if (TryParseCore(baseName, out parsed))
        {
            return true;
        }

        if (!allowStaging ||
            !TryStripStagingSuffix(baseName, out var stagingBase, out var uniqueSuffix) ||
            !TryParseCore(stagingBase, out var stagingParsed))
        {
            return false;
        }

        parsed = stagingParsed! with
        {
            FolderName = baseName,
            IsStaging = true,
            UniqueSuffix = uniqueSuffix
        };
        return true;
    }

    /// <summary>
    /// Parses suburb and state from a cache folder name. Prefer
    /// <see cref="TryParseCacheFolderName"/> when the timestamp is also needed.
    /// </summary>
    public static (string suburb, string state)? ParseLocationFromFilename(string fileName)
    {
        return TryParseCacheFolderName(fileName, out var parsed)
            ? (parsed!.Location.Suburb, parsed.Location.State)
            : null;
    }

    /// <summary>
    /// Parses the UTC timestamp from a cache folder name.
    /// </summary>
    public static DateTime? ParseTimestampFromFilename(string fileName)
    {
        return TryParseCacheFolderName(fileName, out var parsed)
            ? parsed!.CacheTimestampUtc
            : null;
    }

    private static bool TryParseCore(string baseName, out ParsedCacheFolder? parsed)
    {
        parsed = null;

        if (baseName.StartsWith("v2~", StringComparison.Ordinal) &&
            TryParseV2(baseName, out parsed))
        {
            return true;
        }

        // An old legacy folder whose suburb literally starts with "v2~" remains
        // readable. Its trailing "_STATE_date_time" prevents it from satisfying
        // the strict v2 grammar, so this fallback cannot alias a real v2 name.
        return TryParseLegacy(baseName, out parsed);
    }

    private static bool TryParseLegacy(string baseName, out ParsedCacheFolder? parsed)
    {
        parsed = null;
        var parts = baseName.Split('_');
        if (parts.Length < 4)
        {
            return false;
        }

        var state = parts[^3];
        var datePart = parts[^2];
        var timePart = parts[^1];

        if (!DateTime.TryParseExact(
                $"{datePart}_{timePart}",
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        var suburb = string.Join(' ', parts.Take(parts.Length - 3))
            .Replace('_', ' ')
            .Trim();

        if (!LocationId.TryCreate(suburb, state, out var location, out _))
        {
            return false;
        }

        parsed = new ParsedCacheFolder(baseName, location!, timestamp);
        return true;
    }

    private static bool TryParseV2(string baseName, out ParsedCacheFolder? parsed)
    {
        parsed = null;
        var parts = baseName.Split('~');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], "v2", StringComparison.Ordinal) ||
            parts[1].Length == 0 ||
            parts[2].Length == 0)
        {
            return false;
        }

        var suburb = DecodeBase64Url(parts[1]);
        if (suburb is null)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                $"{parts[3]}{parts[4]}",
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        if (!LocationId.TryCreate(suburb, parts[2], out var location, out _) ||
            !string.Equals(location!.Suburb, suburb, StringComparison.Ordinal) ||
            !string.Equals(location.State, parts[2], StringComparison.Ordinal))
        {
            return false;
        }

        // Reject non-canonical encodings and timestamps. This also guarantees
        // GetFinalFolderName -> TryParseCacheFolderName -> exact LocationId.
        if (!string.Equals(EncodeV2(location, timestamp), baseName, StringComparison.Ordinal))
        {
            return false;
        }

        parsed = new ParsedCacheFolder(baseName, location, timestamp);
        return true;
    }

    private static string EncodeV2(LocationId location, DateTime capturedAtUtc)
    {
        var encodedSuburb = Convert.ToBase64String(Encoding.UTF8.GetBytes(location.Suburb))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var date = capturedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var time = capturedAtUtc.ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"v2~{encodedSuburb}~{location.State}~{date}~{time}";
    }

    private static string? DecodeBase64Url(string value)
    {
        foreach (var ch in value)
        {
            var isUpper = ch is >= 'A' and <= 'Z';
            var isLower = ch is >= 'a' and <= 'z';
            var isDigit = ch is >= '0' and <= '9';
            if (!isUpper && !isLower && !isDigit && ch != '-' && ch != '_')
            {
                return null;
            }
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            1 => string.Empty,
            _ => normalized
        };

        if (normalized.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            var text = StrictUtf8.GetString(bytes);
            return text.Length == 0 ? null : text;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool TryStripStagingSuffix(
        string baseName,
        out string stagingBase,
        out string uniqueSuffix)
    {
        stagingBase = string.Empty;
        uniqueSuffix = string.Empty;

        var separator = baseName.LastIndexOf('_');
        if (separator <= 0)
        {
            return false;
        }

        var suffix = baseName[(separator + 1)..];
        if (suffix.Length is < 8 or > 64 || !suffix.All(IsLowerHex))
        {
            return false;
        }

        stagingBase = baseName[..separator];
        uniqueSuffix = suffix;
        return true;
    }

    private static bool ValidateFolderName(string name, out string? validated, out string? error)
    {
        validated = null;
        error = null;

        if (string.IsNullOrWhiteSpace(name) ||
            name != name.Trim() ||
            name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Contains(':') ||
            name.Contains('*') ||
            name.Contains('?') ||
            Path.IsPathRooted(name) ||
            name.Any(char.IsControl))
        {
            error = "Cache folder name is not a valid direct child name";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxFolderNameBytes)
        {
            error = $"Cache folder name exceeds {MaxFolderNameBytes} UTF-8 bytes";
            return false;
        }

        validated = name;
        return true;
    }

    private static bool IsLowerHex(char value) =>
        (value is >= '0' and <= '9') || (value is >= 'a' and <= 'f');
}
