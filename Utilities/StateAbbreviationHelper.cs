using System.Globalization;

namespace BomLocalService.Utilities;

public static class StateAbbreviationHelper
{
    public static readonly string[] SupportedStates = { "QLD", "NSW", "VIC", "SA", "WA", "TAS", "NT", "ACT" };

    public static string SupportedStatesDisplay => string.Join(", ", SupportedStates);

    private static readonly Dictionary<string, string[]> StateMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "qld", new[] { "queensland" } },
        { "nsw", new[] { "new south wales" } },
        { "vic", new[] { "victoria" } },
        { "sa", new[] { "south australia" } },
        { "wa", new[] { "western australia" } },
        { "tas", new[] { "tasmania" } },
        { "nt", new[] { "northern territory" } },
        { "act", new[] { "australian capital territory" } }
    };

    /// <summary>
    /// Normalizes a documented state abbreviation or full state name to its
    /// uppercase abbreviation. Returns false for unknown states.
    /// </summary>
    public static bool TryNormalize(string? state, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        var candidate = string.Join(' ', state!.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpper(CultureInfo.InvariantCulture);

        if (SupportedStates.Contains(candidate, StringComparer.Ordinal))
        {
            normalized = candidate;
            return true;
        }

        var fullName = state.Trim();
        foreach (var (abbreviation, names) in StateMappings)
        {
            if (names.Any(name => string.Equals(name, fullName, StringComparison.OrdinalIgnoreCase)))
            {
                normalized = abbreviation.ToUpperInvariant();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the text matches the state abbreviation or full state name.
    /// </summary>
    public static bool MatchesState(string text, string stateAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(stateAbbreviation))
        {
            return true;
        }

        var textLower = text.ToLowerInvariant();
        if (!TryNormalize(stateAbbreviation, out var normalizedState))
        {
            return false;
        }

        if (textLower.Contains(normalizedState.ToLowerInvariant()))
        {
            return true;
        }

        return StateMappings.TryGetValue(normalizedState, out var fullNames) &&
               fullNames.Any(fullName => textLower.Contains(fullName));
    }
}
