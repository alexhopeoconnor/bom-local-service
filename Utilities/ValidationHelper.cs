using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class ValidationHelper
{
    /// <summary>
    /// Validates and normalizes suburb and state parameters.
    /// Returns an error message if validation fails, or null and a LocationId if valid.
    /// </summary>
    public static string? TryValidateLocation(string? suburb, string? state, out LocationId? location)
    {
        if (!LocationId.TryCreate(suburb, state, out location, out var error))
        {
            return error;
        }

        return null;
    }

    /// <summary>
    /// Validates suburb and state parameters.
    /// Returns an error message if validation fails, null if valid.
    /// </summary>
    public static string? ValidateLocation(string suburb, string state)
    {
        return TryValidateLocation(suburb, state, out _);
    }
}
