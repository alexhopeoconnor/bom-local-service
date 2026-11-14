using System.Text.RegularExpressions;
using BomLocalService.Models;
using BomLocalService.Services.Browser;
using BomLocalService.Services.Interfaces;
using Microsoft.Playwright;

namespace BomLocalService.Services.Scraping;

/// <summary>
/// Parses the BOM scrubber time label (.bom-scrub-display-label) to UTC.
/// Shared by capture and reset steps so behaviour stays in sync.
/// </summary>
public class ScrubDisplayTimestampParser : IScrubDisplayTimestampParser
{
    private static readonly TimeSpan ReadOperationTimeout = TimeSpan.FromSeconds(10);

    private readonly IConfiguration _configuration;
    private readonly ScrapingSelectorsConfig _selectors;
    private readonly TextPatternsConfig _textPatterns;
    private readonly JavaScriptTemplatesConfig _javaScriptTemplates;
    private readonly ILogger<ScrubDisplayTimestampParser> _logger;

    public ScrubDisplayTimestampParser(
        IConfiguration configuration,
        ILogger<ScrubDisplayTimestampParser> logger)
    {
        _configuration = configuration;
        _selectors = configuration.GetSection("Scraping:Selectors").Get<ScrapingSelectorsConfig>() ?? new();
        _textPatterns = configuration.GetSection("Scraping:TextPatterns").Get<TextPatternsConfig>() ?? new();
        _javaScriptTemplates = configuration.GetSection("Scraping:JavaScriptTemplates").Get<JavaScriptTemplatesConfig>() ?? new();
        _logger = logger;
    }

    public async Task<DateTime?> ReadUtcAsync(
        IPage page,
        ISelectorService selectorService,
        ScrapingContext? context = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var timeLabelLocator = selectorService.GetLocator(page, _selectors.TimeDisplayLabel);
            var timeLabel = await BoundedBrowserOperation.RunAsync(
                timeLabelLocator.TextContentAsync(),
                ReadOperationTimeout,
                cancellationToken,
                reclaim: null,
                observe: (message, exception) => _logger.LogDebug(exception, "{Message}", message),
                operationName: "read-scrub-display-label").ConfigureAwait(false);
            if (string.IsNullOrEmpty(timeLabel))
            {
                _logger.LogDebug("Time display label is empty");
                return null;
            }

            var trimmedLabel = timeLabel.Trim();
            _logger.LogDebug("Scrub display label: '{Label}'", trimmedLabel);

            var timestampMatch = Regex.Match(trimmedLabel, _textPatterns.TimestampPattern, RegexOptions.IgnoreCase);
            if (!timestampMatch.Success)
            {
                _logger.LogWarning(
                    "Display label did not match timestamp pattern. Label: '{Label}', Pattern: '{Pattern}'",
                    trimmedLabel,
                    _textPatterns.TimestampPattern);
                return null;
            }

            var timestampStr = timestampMatch.Groups[0].Value;

            string? detectedTimezone = null;
            if (trimmedLabel.Contains("AEDT", StringComparison.OrdinalIgnoreCase))
                detectedTimezone = "AEDT";
            else if (trimmedLabel.Contains("AEST", StringComparison.OrdinalIgnoreCase))
                detectedTimezone = "AEST";

            if (detectedTimezone == null && context != null)
            {
                try
                {
                    var metadataText = await BoundedBrowserOperation.RunAsync(
                        page.EvaluateAsync<string>(_javaScriptTemplates.ExtractWeatherMetadata),
                        ReadOperationTimeout,
                        cancellationToken,
                        reclaim: null,
                        observe: (message, exception) => _logger.LogDebug(exception, "{Message}", message),
                        operationName: "read-scrub-display-metadata").ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(metadataText))
                    {
                        if (metadataText.Contains("AEDT", StringComparison.OrdinalIgnoreCase))
                            detectedTimezone = "AEDT";
                        else if (metadataText.Contains("AEST", StringComparison.OrdinalIgnoreCase))
                            detectedTimezone = "AEST";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "Scrub display metadata read was cancelled",
                        ex,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to extract timezone from metadata, using configured timezone");
                }
            }

            if (TryParseTimestamp(timestampStr, detectedTimezone, out var frameTimestampUtc))
                return frameTimestampUtc;

            _logger.LogWarning("Failed to parse timestamp string: '{Timestamp}'", timestampStr);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Scrub display label read was cancelled",
                ex,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract timestamp from display label");
            return null;
        }
    }

    public bool TryParseTimestamp(string timestampStr, string? timezoneAbbreviation, out DateTime timestampUtc)
    {
        timestampUtc = DateTime.MinValue;

        try
        {
            TimeZoneInfo timeZoneInfo;

            if (!string.IsNullOrEmpty(timezoneAbbreviation))
            {
                if (timezoneAbbreviation.Contains("AEST", StringComparison.OrdinalIgnoreCase))
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
                else if (timezoneAbbreviation.Contains("AEDT", StringComparison.OrdinalIgnoreCase))
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
                else
                {
                    var timezone = _configuration.GetValue<string>("Timezone");
                    if (string.IsNullOrEmpty(timezone))
                        return false;
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                }
            }
            else
            {
                var timezone = _configuration.GetValue<string>("Timezone");
                if (string.IsNullOrEmpty(timezone))
                    return false;
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }

            timestampStr = Regex.Replace(timestampStr.Trim(), @"\s+", " ");

            var formats = new[]
            {
                "dddd d MMM, h:mm tt",
                "d MMM, h:mm tt",
                "dddd d MMM, hh:mm tt",
                "d MMM, hh:mm tt",
                "dddd dd MMM, h:mm tt",
                "dd MMM, h:mm tt",
                "dddd d MMM, h:mmtt",
                "d MMM, h:mmtt",
                "dddd d MMM, hh:mmtt",
                "d MMM, hh:mmtt",
                "dddd dd MMM, h:mmtt",
                "dd MMM, h:mmtt",
                "dddd dd MMM, hh:mmtt",
                "dd MMM, hh:mmtt"
            };

            var culture = new System.Globalization.CultureInfo("en-AU");
            DateTime localTime = default;
            var parsed = false;

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(timestampStr, format, culture,
                        System.Globalization.DateTimeStyles.None, out localTime))
                {
                    parsed = true;
                    break;
                }
            }

            if (!parsed)
                return false;

            if (localTime.Year == 1)
            {
                localTime = new DateTime(DateTime.UtcNow.Year, localTime.Month, localTime.Day,
                    localTime.Hour, localTime.Minute, localTime.Second);
            }

            timestampUtc = TimeZoneInfo.ConvertTimeToUtc(localTime, timeZoneInfo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse timestamp: {Timestamp}", timestampStr);
            return false;
        }
    }

    public async Task WaitForDisplayLabelChangeAsync(
        IPage page,
        ISelectorService selectorService,
        DateTime currentTimestamp,
        ScrapingContext? context = null,
        int maxWaitMs = 5000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxWaitMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newTimestamp = await ReadUtcAsync(page, selectorService, context, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (newTimestamp.HasValue && newTimestamp.Value != currentTimestamp)
                    return;

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug(
                "Display label did not change from {CurrentTimestamp} within {MaxWaitMs}ms",
                currentTimestamp,
                maxWaitMs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Waiting for the scrub display label was cancelled",
                ex,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error waiting for display label to change");
        }
    }
}
