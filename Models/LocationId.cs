using System.Globalization;
using System.Text;
using BomLocalService.Utilities;

namespace BomLocalService.Models;

/// <summary>
/// A validated, normalized radar location. Identity normalization is deliberately
/// separate from the on-disk folder name so existing cache folders keep working.
/// </summary>
public sealed class LocationId : IEquatable<LocationId>
{
    public const int MaxSuburbLength = 120;
    private const int MaxCacheBasenameBytes = 255;
    private const int StagingSuffixBytes = 8;

    private LocationId(string suburb, string state)
    {
        Suburb = suburb;
        State = state;
        CanonicalSuburb = NormalizeCanonicalSuburb(suburb);
        Key = $"{CanonicalSuburb}|{state}";
    }

    /// <summary>Display suburb, trimmed and whitespace-normalized.</summary>
    public string Suburb { get; }

    /// <summary>Uppercase Australian state abbreviation (QLD, NSW, ...).</summary>
    public string State { get; }

    /// <summary>Case-folded canonical suburb used only for identity comparison.</summary>
    public string CanonicalSuburb { get; }

    /// <summary>Stable identity key used for dictionaries and state files.</summary>
    public string Key { get; }

    public static bool TryCreate(string? suburb, string? state, out LocationId? location, out string? error)
    {
        location = null;
        error = null;

        if (string.IsNullOrWhiteSpace(suburb))
        {
            error = "Suburb is required";
            return false;
        }

        if (suburb.Any(char.IsControl))
        {
            error = "Suburb must not contain control characters";
            return false;
        }

        var normalizedSuburb = NormalizeSuburb(suburb);
        if (normalizedSuburb.Length == 0)
        {
            error = "Suburb is required";
            return false;
        }

        if (normalizedSuburb.Length > MaxSuburbLength)
        {
            error = $"Suburb must be at most {MaxSuburbLength} characters";
            return false;
        }

        if (normalizedSuburb is "." or "..")
        {
            error = "Suburb is not a valid location name";
            return false;
        }

        foreach (var ch in normalizedSuburb)
        {
            if (char.IsControl(ch))
            {
                error = "Suburb must not contain control characters";
                return false;
            }

            if (ch is '*' or '?' or '/' or '\\' or ':' or '"' or '<' or '>' or '|' or '\0')
            {
                error = "Suburb contains characters that are not valid in a location name";
                return false;
            }
        }

        if (!StateAbbreviationHelper.TryNormalize(state, out var normalizedState))
        {
            error = $"State must be one of {StateAbbreviationHelper.SupportedStatesDisplay}";
            return false;
        }

        if (!TryValidateCacheBasenameCapacity(normalizedSuburb, normalizedState, out error))
        {
            return false;
        }

        location = new LocationId(normalizedSuburb, normalizedState);
        return true;
    }

    public static LocationId Create(string suburb, string state)
    {
        if (!TryCreate(suburb, state, out var location, out var error))
        {
            throw new ArgumentException(error, nameof(suburb));
        }

        return location!;
    }

    /// <summary>
    /// Normalizes display whitespace and Unicode composition without changing
    /// punctuation that is valid in Australian place names.
    /// </summary>
    public static string NormalizeSuburb(string suburb)
    {
        if (string.IsNullOrWhiteSpace(suburb))
        {
            return string.Empty;
        }

        var normalized = suburb.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Rejects a location whose final or actual staging basename would exceed the
    /// filesystem name limit. This is deliberately non-recursive: it must not call
    /// the folder parser, because that parser calls TryCreate.
    /// </summary>
    private static bool TryValidateCacheBasenameCapacity(
        string suburb,
        string state,
        out string? error)
    {
        error = null;

        // Legacy underscores represent spaces. A literal underscore or comma is
        // lossy, and a leading dot would be treated as a reserved cache directory,
        // so LocationHelper will use the reversible v2 shape.
        var usesLegacy = !suburb.StartsWith(".", StringComparison.Ordinal) &&
                         !suburb.Contains('_') &&
                         !suburb.Contains(',');
        string basename;
        if (usesLegacy)
        {
            basename = $"{SanitizeLegacyPrefix($"{suburb}_{state}")}_20000101_000000";
        }
        else
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(suburb))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            basename = $"v2~{encoded}~{state}~20000101~000000";
        }

        var finalBytes = Encoding.UTF8.GetByteCount(basename);
        var stagingBytes = finalBytes + 1 + StagingSuffixBytes;
        if (finalBytes > MaxCacheBasenameBytes || stagingBytes > MaxCacheBasenameBytes)
        {
            error = "Location cannot be represented in a cache folder name: final or staging basename exceeds 255 UTF-8 bytes";
            return false;
        }

        return true;
    }

    private static string SanitizeLegacyPrefix(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" ", "_")
            .Replace(",", "");
    }

    /// <summary>
    /// Canonical identity treats case and whitespace consistently. Cache folder
    /// names use '_' for spaces, but existing casing on disk is preserved.
    /// </summary>
    private static string NormalizeCanonicalSuburb(string suburb)
    {
        var normalized = NormalizeSuburb(suburb).Normalize(NormalizationForm.FormC);
        return normalized.ToLower(CultureInfo.InvariantCulture);
    }

    public bool Equals(LocationId? other)
    {
        return other is not null &&
               string.Equals(Key, other.Key, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as LocationId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    public override string ToString() => $"{Suburb}, {State}";
}
