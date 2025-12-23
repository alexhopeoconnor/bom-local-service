using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;
using BomLocalService.Utilities;
using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace BomLocalService.Services.Scraping.Steps.Capture;

public class CaptureFramesStep : BaseScrapingStep
{
    private readonly ICacheService _cacheService;
    private readonly int _tileRenderWaitMs;
    private readonly ScreenshotCropConfig _cropConfig;
    
    public override string Name => "CaptureFrames";
    public override string[] Prerequisites => new[] { "ExtractMetadata", "CalculateMapBounds" };
    
    public CaptureFramesStep(
        ILogger<CaptureFramesStep> logger,
        ISelectorService selectorService,
        IDebugService debugService,
        IConfiguration configuration,
        ICacheService cacheService)
        : base(logger, selectorService, debugService, configuration)
    {
        _cacheService = cacheService;
        
        var tileRenderWaitMsConfig = configuration.GetValue<int?>("Screenshot:TileRenderWaitMs");
        if (!tileRenderWaitMsConfig.HasValue)
        {
            throw new InvalidOperationException("Screenshot:TileRenderWaitMs configuration is required. Set it in appsettings.json or via SCREENSHOT__TILERENDERWAITMS environment variable.");
        }
        _tileRenderWaitMs = tileRenderWaitMsConfig.Value;
        
        var cropSection = configuration.GetSection("Screenshot:Crop");
        
        var cropXConfig = cropSection.GetValue<int?>("X");
        if (!cropXConfig.HasValue)
        {
            throw new InvalidOperationException("Screenshot:Crop:X configuration is required. Set it in appsettings.json or via SCREENSHOT__CROP__X environment variable.");
        }
        var cropYConfig = cropSection.GetValue<int?>("Y");
        if (!cropYConfig.HasValue)
        {
            throw new InvalidOperationException("Screenshot:Crop:Y configuration is required. Set it in appsettings.json or via SCREENSHOT__CROP__Y environment variable.");
        }
        var cropRightOffsetConfig = cropSection.GetValue<int?>("RightOffset");
        if (!cropRightOffsetConfig.HasValue)
        {
            throw new InvalidOperationException("Screenshot:Crop:RightOffset configuration is required. Set it in appsettings.json or via SCREENSHOT__CROP__RIGHTOFFSET environment variable.");
        }
        
        _cropConfig = new ScreenshotCropConfig
        {
            X = cropXConfig.Value,
            Y = cropYConfig.Value,
            RightOffset = cropRightOffsetConfig.Value,
            Height = cropSection.GetValue<int?>("Height") // Height is optional (nullable)
        };
    }
    
    public override bool CanExecute(ScrapingContext context)
    {
        return context.MapBoundingBox != null && context.MapContainer != null;
    }
    
    public override async Task<ScrapingStepResult> ExecuteAsync(ScrapingContext context, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(context.CacheFolderPath);
            Logger.LogInformation("Step {Step}: Using cache folder: {Path}", Name, context.CacheFolderPath);
            
            var frameCount = CacheHelper.GetFrameCountForDataType(Configuration, CachedDataType.Radar);
            
            _cacheService.RecordUpdateProgressByFolder(context.CacheFolderPath, CacheUpdatePhase.CapturingFrames, 0, frameCount);
            
            var frames = new List<RadarFrame>();
            var stepForwardButton = SelectorService.GetLocator(context.Page, Selectors.StepForwardButton);
            DateTime? previousTimestamp = null;
            
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                Logger.LogInformation("Step {Step}: Capturing frame {FrameIndex} of {FrameCount}", Name, frameIndex, frameCount);
                
                await context.Page.WaitForTimeoutAsync(_tileRenderWaitMs);
                
                // Small wait to ensure display label is stable after frame change/selection
                // This is especially important for frame 0 which was just selected in ResetToFirstFrame
                await context.Page.WaitForTimeoutAsync(300);
                
                // Try extracting timestamp with a retry in case the label is still updating
                var frameTimestamp = await ExtractTimestampFromDisplayAsync(context.Page);
                if (frameTimestamp == null)
                {
                    // Retry once after a short wait in case label was updating
                    await context.Page.WaitForTimeoutAsync(200);
                    frameTimestamp = await ExtractTimestampFromDisplayAsync(context.Page);
                }
                
                // Fallback: calculate expected timestamp from observation time and frame index if we can't parse it
                if (frameTimestamp == null && context.LastUpdatedInfo?.ObservationTime != null && context.FrameInfo != null && frameIndex < context.FrameInfo.Count)
                {
                    var (_, defaultMinutesAgo) = context.FrameInfo[frameIndex];
                    frameTimestamp = context.LastUpdatedInfo.ObservationTime.AddMinutes(-defaultMinutesAgo);
                    Logger.LogWarning("Step {Step}: Failed to extract timestamp from display label for frame {FrameIndex}, calculated from observation time: {Timestamp}", 
                        Name, frameIndex, frameTimestamp);
                }
                
                if (frameIndex > 0 && previousTimestamp.HasValue && frameTimestamp == previousTimestamp.Value)
                {
                    Logger.LogWarning("Step {Step}: Frame {FrameIndex} has same timestamp ({Timestamp}) as previous frame. Waiting for display to update...", 
                        Name, frameIndex, frameTimestamp);
                    await WaitForDisplayLabelToChangeAsync(context.Page, previousTimestamp.Value);
                    frameTimestamp = await ExtractTimestampFromDisplayAsync(context.Page);
                    if (frameTimestamp == null || frameTimestamp == previousTimestamp.Value)
                    {
                        if (context.LastUpdatedInfo?.ObservationTime != null && context.FrameInfo != null && frameIndex < context.FrameInfo.Count)
                        {
                            var (_, defaultMinutesAgo) = context.FrameInfo[frameIndex];
                            frameTimestamp = context.LastUpdatedInfo.ObservationTime.AddMinutes(-defaultMinutesAgo);
                            Logger.LogWarning("Step {Step}: Display label did not update for frame {FrameIndex}, calculated from observation time: {Timestamp}", 
                                Name, frameIndex, frameTimestamp);
                        }
                    }
                }
                
                if (frameTimestamp == null)
                {
                    Logger.LogError("Step {Step}: Could not determine timestamp for frame {FrameIndex}, skipping", Name, frameIndex);
                    continue;
                }
                
                var radarFolder = FilePathHelper.GetDataTypeFolderPath(context.CacheFolderPath, CachedDataType.Radar);
                if (!Directory.Exists(radarFolder))
                {
                    Directory.CreateDirectory(radarFolder);
                }
                
                var framePath = FilePathHelper.GetFrameFilePath(context.CacheFolderPath, CachedDataType.Radar, frameIndex);
                await CaptureMapScreenshotAsync(context.Page, context.MapContainer!, framePath, context.MapBoundingBox!);
                
                frames.Add(new RadarFrame
                {
                    FrameIndex = frameIndex,
                    ImagePath = framePath,
                    AbsoluteObservationTime = frameTimestamp.Value
                });
                
                previousTimestamp = frameTimestamp;
                
                Logger.LogInformation("Step {Step}: Frame {FrameIndex} saved: {Path} (timestamp: {Timestamp} UTC)", 
                    Name, frameIndex, framePath, frameTimestamp.Value);
                
                _cacheService.RecordUpdateProgressByFolder(context.CacheFolderPath, CacheUpdatePhase.CapturingFrames, frameIndex + 1, frameCount);
                
                await SaveDebugAsync(context, 15 + frameIndex, $"frame_{frameIndex}_captured", cancellationToken);
                
                if (frameIndex < frameCount - 1)
                {
                    await DismissModalOverlaysAsync(context.Page);
                    
                    var currentTimestamp = await ExtractTimestampFromDisplayAsync(context.Page);
                    
                    await stepForwardButton.ClickAsync(new LocatorClickOptions { Force = true });
                    
                    if (currentTimestamp.HasValue)
                    {
                        await WaitForDisplayLabelToChangeAsync(context.Page, currentTimestamp.Value);
                    }
                    else
                    {
                        await context.Page.WaitForTimeoutAsync(500);
                    }
                }
            }
            
            Logger.LogInformation("Step {Step}: All {FrameCount} frames captured successfully", Name, frameCount);
            
            _cacheService.RecordUpdateProgressByFolder(context.CacheFolderPath, CacheUpdatePhase.Saving);
            
            if (context.LastUpdatedInfo != null)
            {
                await _cacheService.SaveMetadataAsync(context.CacheFolderPath, context.LastUpdatedInfo, cancellationToken);
            }
            await _cacheService.SaveFramesMetadataAsync(context.CacheFolderPath, CachedDataType.Radar, frames, cancellationToken);
            
            context.Frames = frames;
            
            return ScrapingStepResult.Successful();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Step {Step} failed", Name);
            await SaveErrorDebugAsync(context, $"Failed to capture frames: {ex.Message}", cancellationToken);
            return ScrapingStepResult.Failed($"Failed to capture frames: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Extracts the UTC timestamp from the frame display label
    /// </summary>
    private async Task<DateTime?> ExtractTimestampFromDisplayAsync(IPage page)
    {
        try
        {
            var timeLabelLocator = SelectorService.GetLocator(page, Selectors.TimeDisplayLabel);
            var timeLabel = await timeLabelLocator.TextContentAsync();
            if (string.IsNullOrEmpty(timeLabel))
            {
                Logger.LogDebug("Time display label is empty");
                return null;
            }
            
            var trimmedLabel = timeLabel.Trim();
            Logger.LogInformation("Extracting timestamp from display label: '{Label}'", trimmedLabel);
            
            // Parse timestamp format (current BOM website format): "Wednesday 17 Dec, 11:05 pm" or "17 Dec, 11:05 pm"
            Logger.LogInformation("Trying timestamp pattern: '{Pattern}'", TextPatterns.TimestampPattern);
            var timestampMatch = Regex.Match(trimmedLabel, TextPatterns.TimestampPattern, RegexOptions.IgnoreCase);
            if (timestampMatch.Success)
            {
                var timestampStr = timestampMatch.Groups[0].Value;
                Logger.LogInformation("Matched timestamp pattern: '{Timestamp}' from label: '{Label}'", timestampStr, trimmedLabel);
                if (TryParseTimestamp(timestampStr, out var frameTimestampUtc))
                {
                    Logger.LogInformation("Successfully parsed frame timestamp: {Timestamp} UTC", frameTimestampUtc);
                    return frameTimestampUtc;
                }
                else
                {
                    Logger.LogWarning("Failed to parse timestamp string: '{Timestamp}'", timestampStr);
                }
            }
            else
            {
                Logger.LogWarning("Display label did not match timestamp pattern. Label: '{Label}', Pattern: '{Pattern}'", trimmedLabel, TextPatterns.TimestampPattern);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to extract timestamp from display label");
            return null;
        }
    }
    
    private bool TryParseTimestamp(string timestampStr, out DateTime timestampUtc)
    {
        timestampUtc = DateTime.MinValue;
        
        try
        {
            // Get configured timezone
            var timezone = Configuration.GetValue<string>("Timezone");
            if (string.IsNullOrEmpty(timezone))
            {
                Logger.LogWarning("Timezone not configured, cannot parse timestamp correctly");
                return false;
            }
            
            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            
            // Try common Australian date formats
            // Format: "Wednesday 17 Dec, 11:05 pm" or "17 Dec, 11:05 pm"
            var formats = new[]
            {
                "dddd d MMM, h:mm tt",      // Wednesday 17 Dec, 11:05 pm
                "d MMM, h:mm tt",           // 17 Dec, 11:05 pm
                "dddd d MMM, hh:mm tt",     // Wednesday 17 Dec, 11:05 pm (with leading zero)
                "d MMM, hh:mm tt",          // 17 Dec, 11:05 pm (with leading zero)
                "dddd dd MMM, h:mm tt",     // Wednesday 17 Dec, 11:05 pm (with leading zero day)
                "dd MMM, h:mm tt"           // 17 Dec, 11:05 pm (with leading zero day)
            };
            
            var culture = new System.Globalization.CultureInfo("en-AU");
            DateTime localTime = default;
            bool parsed = false;
            
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
            {
                return false;
            }
            
            // If year is not specified, assume current year
            if (localTime.Year == 1)
            {
                localTime = new DateTime(DateTime.Now.Year, localTime.Month, localTime.Day, 
                    localTime.Hour, localTime.Minute, localTime.Second);
            }
            
            // Get current time in the configured timezone to determine the date
            var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);
            var dateTimeInTz = nowInTz.Date.Add(localTime.TimeOfDay);
            
            // If the time is in the future, it must be from yesterday
            if (dateTimeInTz > nowInTz)
            {
                dateTimeInTz = dateTimeInTz.AddDays(-1);
            }
            
            // Convert to UTC
            timestampUtc = TimeZoneInfo.ConvertTimeToUtc(dateTimeInTz, timeZoneInfo);
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse timestamp: {Timestamp}", timestampStr);
            return false;
        }
    }
    
    private async Task WaitForDisplayLabelToChangeAsync(IPage page, DateTime currentTimestamp, int maxWaitMs = 5000)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxWaitMs)
            {
                var newTimestamp = await ExtractTimestampFromDisplayAsync(page);
                if (newTimestamp.HasValue && newTimestamp.Value != currentTimestamp)
                {
                    return;
                }
                await page.WaitForTimeoutAsync(200);
            }
            Logger.LogDebug("Display label did not change from {CurrentTimestamp} within {MaxWaitMs}ms", currentTimestamp, maxWaitMs);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error waiting for display label to change");
        }
    }
    
    private async Task DismissModalOverlaysAsync(IPage page)
    {
        try
        {
            var hasModal = await page.EvaluateAsync<bool>(JavaScriptTemplates.CheckModalOverlay);
            
            if (!hasModal)
            {
                return;
            }
            
            Logger.LogDebug("Modal overlay detected, dismissing");
            
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(200);
            
            var stillVisible = await page.EvaluateAsync<bool>(JavaScriptTemplates.CheckModalStillVisible);
            
            if (stillVisible)
            {
                try
                {
                    var mapContainer = SelectorService.GetLocator(page, Selectors.MapContainer);
                    await mapContainer.ClickAsync(new LocatorClickOptions { Force = true });
                    await page.WaitForTimeoutAsync(100);
                }
                catch
                {
                    // Ignore if click fails
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error dismissing modal overlay, continuing");
        }
    }
    
    private async Task CaptureMapScreenshotAsync(IPage page, ILocator mapContainer, string outputPath, Clip containerClip)
    {
        if (containerClip == null || containerClip.Width <= 0 || containerClip.Height <= 0)
        {
            Logger.LogError("Invalid container bounds: X={X}, Y={Y}, Width={Width}, Height={Height}", 
                containerClip?.X ?? 0, containerClip?.Y ?? 0, containerClip?.Width ?? 0, containerClip?.Height ?? 0);
            throw new Exception($"Invalid container bounds: {containerClip?.Width ?? 0}x{containerClip?.Height ?? 0}");
        }
        
        Clip cropArea;
        try
        {
            cropArea = CalculateCropArea(containerClip);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to calculate crop area, using full container bounds");
            cropArea = containerClip;
        }
        
        var viewportSize = page.ViewportSize;
        int? viewportWidth = viewportSize?.Width;
        int? viewportHeight = viewportSize?.Height;
        
        if (viewportWidth == null || viewportHeight == null)
        {
            try
            {
                var viewportJson = await page.EvaluateAsync<string>(JavaScriptTemplates.GetViewportSize);
                if (!string.IsNullOrEmpty(viewportJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(viewportJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("width", out var widthProp) && root.TryGetProperty("height", out var heightProp))
                    {
                        if (widthProp.TryGetInt32(out var width) && heightProp.TryGetInt32(out var height))
                        {
                            viewportWidth = width;
                            viewportHeight = height;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to get viewport size from page evaluation");
            }
            
            if (viewportWidth == null || viewportHeight == null)
            {
                viewportWidth = (int)containerClip.Width;
                viewportHeight = (int)containerClip.Height;
            }
        }
        
        if (viewportWidth.HasValue && viewportHeight.HasValue)
        {
            if (cropArea.X < 0)
            {
                cropArea = new Clip { X = 0, Y = cropArea.Y, Width = cropArea.Width + cropArea.X, Height = cropArea.Height };
            }
            if (cropArea.Y < 0)
            {
                cropArea = new Clip { X = cropArea.X, Y = 0, Width = cropArea.Width, Height = cropArea.Height + cropArea.Y };
            }
            
            if (cropArea.X + cropArea.Width > viewportWidth.Value)
            {
                var newWidth = viewportWidth.Value - cropArea.X;
                cropArea = new Clip { X = cropArea.X, Y = cropArea.Y, Width = newWidth, Height = cropArea.Height };
            }
            if (cropArea.Y + cropArea.Height > viewportHeight.Value)
            {
                var newHeight = viewportHeight.Value - cropArea.Y;
                cropArea = new Clip { X = cropArea.X, Y = cropArea.Y, Width = cropArea.Width, Height = newHeight };
            }
        }
        
        if (cropArea.Width <= 0 || cropArea.Height <= 0)
        {
            Logger.LogError("Invalid crop dimensions after validation: {Width}x{Height}, using full container", cropArea.Width, cropArea.Height);
            cropArea = containerClip;
        }
        
        if (cropArea.Width <= 0 || cropArea.Height <= 0)
        {
            throw new Exception($"Cannot create valid crop area. Container: {containerClip.Width}x{containerClip.Height}, Viewport: {viewportWidth ?? 0}x{viewportHeight ?? 0}");
        }
        
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
        }
        catch
        {
            // Continue if network idle timeout
        }
        
        await DismissModalOverlaysAsync(page);
        
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            Clip = cropArea,
            Type = ScreenshotType.Png,
            Animations = ScreenshotAnimations.Disabled
        });
        
        Logger.LogDebug("Screenshot saved: {Path} (crop: {X},{Y} {Width}x{Height})", 
            outputPath, cropArea.X, cropArea.Y, cropArea.Width, cropArea.Height);
    }
    
    private Clip CalculateCropArea(Clip containerClip)
    {
        var x = containerClip.X + _cropConfig.X;
        var y = containerClip.Y + _cropConfig.Y;
        var width = Math.Max(0, containerClip.Width - _cropConfig.X - _cropConfig.RightOffset);
        var height = _cropConfig.Height ?? Math.Max(0, containerClip.Height - _cropConfig.Y);
        
        if (x < containerClip.X || y < containerClip.Y)
        {
            x = containerClip.X;
            y = containerClip.Y;
        }
        
        var maxWidth = containerClip.Width - (x - containerClip.X);
        var maxHeight = containerClip.Height - (y - containerClip.Y);
        
        if (width > maxWidth)
        {
            width = maxWidth;
        }
        
        if (height > maxHeight)
        {
            height = maxHeight;
        }
        
        if (width <= 0 || height <= 0)
        {
            throw new Exception($"Invalid crop dimensions: {width}x{height}");
        }
        
        return new Clip
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }
}

