using System.Globalization;
using BomLocalService.Models;

namespace BomLocalService.Utilities;

public sealed record ParsedCacheFolder(string FolderName, LocationId Location, DateTime CacheTimestampUtc);

public static class LocationHelper
{
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
    /// Sanitizes a filename by removing invalid characters and replacing spaces/commas.
    /// This mirrors the historical on-disk naming convention and must not be changed
    /// without migrating existing cache folders.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" ", "_")
            .Replace(",", "");
    }

    public static string GetCacheFolderPrefix(LocationId location) =>
        SanitizeFileName(GetLocationKey(location));

    /// <summary>
    /// Parses the complete cache basename, including names containing periods.
    /// Format: Suburb_State_YYYYMMDD_HHMMSS. Spaces in the original suburb are
    /// represented by underscores.
    /// </summary>
    public static bool TryParseCacheFolderName(string? folderName, out ParsedCacheFolder? parsed)
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

        // Use the complete basename. Path.GetFileNameWithoutExtension would treat
        // a period in a suburb such as "St. Lucia" as a file extension separator.
        var baseName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(baseName) || baseName == "." || baseName == "..")
        {
            return false;
        }

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
}
