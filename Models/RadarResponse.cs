namespace BomLocalService.Models;

/// <summary>
/// Response model containing all radar screenshot frames and associated metadata.
/// This is the primary response returned when requesting a radar screenshot for a location.
/// </summary>
public class RadarResponse
{
    /// <summary>
    /// List of all captured frames (typically 7 frames: 0-6).
    /// Each frame contains an absoluteObservationTime UTC timestamp.
    /// Client should calculate "minutes ago" dynamically from the timestamp.
    /// </summary>
    public List<RadarFrame> Frames { get; set; } = new();

    /// <summary>
    /// The UTC timestamp when the screenshot file was last written/modified on disk.
    /// This is the file system modification time, which typically matches when the screenshot was captured.
    /// Used as a fallback if metadata is not available.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// The UTC timestamp when the weather observations were taken (from BOM website).
    /// This comes from the LastUpdatedInfo metadata extracted from the BOM weather map page.
    /// BOM updates observations every 15 minutes (at :00, :15, :30, :45 past the hour).
    /// If metadata is not available, defaults to DateTime.UtcNow.
    /// </summary>
    public DateTime ObservationTime { get; set; }

    /// <summary>
    /// The UTC timestamp when the weather forecast was generated (from BOM website).
    /// This comes from the LastUpdatedInfo metadata extracted from the BOM weather map page.
    /// Forecasts are typically updated less frequently than observations.
    /// If metadata is not available, defaults to DateTime.UtcNow.
    /// </summary>
    public DateTime ForecastTime { get; set; }

    /// <summary>
    /// The name of the weather station providing the observations for this location.
    /// Extracted from the BOM website metadata (e.g., "Gympie", "Brisbane").
    /// May be null if the station name cannot be parsed or if metadata is not available.
    /// </summary>
    public string? WeatherStation { get; set; }

    /// <summary>
    /// The distance from the requested location to the weather station.
    /// Extracted from the BOM website metadata (e.g., "30 km", "15 km").
    /// Format: "{number} km".
    /// May be null if the distance cannot be parsed or if metadata is not available.
    /// </summary>
    public string? Distance { get; set; }

    /// <summary>
    /// Indicates whether the cached data is still considered valid based on its observation time
    /// and the configured cache expiration period.
    /// </summary>
    public bool CacheIsValid { get; set; }

    /// <summary>
    /// The UTC date and time when the current cached data is expected to expire.
    /// This is calculated based on the observation time and a configured buffer (e.g., 15.5 minutes).
    /// Null if cache is not valid or metadata is not available.
    /// </summary>
    public DateTime? CacheExpiresAt { get; set; }

    /// <summary>
    /// Indicates whether a cache update is currently in progress for this location.
    /// When true, clients should wait before requesting a refresh, as a new cache is being generated.
    /// </summary>
    public bool IsUpdating { get; set; }

    /// <summary>
    /// The UTC date and time when the next cache update is expected or recommended.
    /// - If cache is valid: equals <see cref="CacheExpiresAt"/> (check again when cache expires).
    /// - If cache is invalid and an update is in progress: estimated completion time (approximately 2 minutes from now).
    /// - If cache is invalid and no update is in progress: null (client should trigger an update).
    /// This may differ from <see cref="CacheExpiresAt"/> when an update is actively in progress.
    /// </summary>
    public DateTime? NextUpdateTime { get; set; }
}

