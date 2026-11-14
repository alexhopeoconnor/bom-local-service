using BomLocalService.Models;
using BomLocalService.Services.Browser;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services.Scraping.Steps.Capture;

public class CaptureFramesStep : BaseScrapingStep
{
    private const int ScreenshotTimeoutMs = 30_000;
    private static readonly TimeSpan ErrorDebugSaveTimeout = TimeSpan.FromSeconds(5);

    private readonly ICacheService _cacheService;
    private readonly IScrubDisplayTimestampParser _scrubDisplayTime;
    private readonly int _tileRenderWaitMs;
    private readonly ScreenshotCropConfig _cropConfig;
    
    public override string Name => "CaptureFrames";
    public override string[] Prerequisites => new[] { "ExtractMetadata", "CalculateMapBounds" };
    
    public CaptureFramesStep(
        ILogger<CaptureFramesStep> logger,
        ISelectorService selectorService,
        IDebugService debugService,
        IConfiguration configuration,
        ICacheService cacheService,
        IScrubDisplayTimestampParser scrubDisplayTime)
        : base(logger, selectorService, debugService, configuration)
    {
        _cacheService = cacheService;
        _scrubDisplayTime = scrubDisplayTime;
        
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
                cancellationToken.ThrowIfCancellationRequested();
                Logger.LogInformation("Step {Step}: Capturing frame {FrameIndex} of {FrameCount}", Name, frameIndex, frameCount);
                
                await Task.Delay(_tileRenderWaitMs, cancellationToken);
                
                // Small wait to ensure display label is stable after frame change/selection
                // This is especially important for frame 0 which was just selected in ResetToFirstFrame
                await Task.Delay(300, cancellationToken);
                
                // Try extracting timestamp with a retry in case the label is still updating
                var frameTimestamp = await _scrubDisplayTime.ReadUtcAsync(context.Page, SelectorService, context, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (frameTimestamp == null)
                {
                    // Retry once after a short wait in case label was updating
                    await Task.Delay(200, cancellationToken);
                    frameTimestamp = await _scrubDisplayTime.ReadUtcAsync(context.Page, SelectorService, context, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
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
                    await _scrubDisplayTime.WaitForDisplayLabelChangeAsync(
                        context.Page, SelectorService, previousTimestamp.Value, context, cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    frameTimestamp = await _scrubDisplayTime.ReadUtcAsync(context.Page, SelectorService, context, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
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
                await CaptureMapScreenshotAsync(context.Page, context.MapContainer!, framePath, context.MapBoundingBox!, cancellationToken);
                
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
                    await DismissModalOverlaysAsync(context.Page, cancellationToken);
                    
                    var currentTimestamp = await _scrubDisplayTime.ReadUtcAsync(context.Page, SelectorService, context, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    await stepForwardButton.ClickAsync(new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = Selectors.StepForwardButton.TimeoutMs
                    });
                    cancellationToken.ThrowIfCancellationRequested();

                    if (currentTimestamp.HasValue)
                    {
                        await _scrubDisplayTime.WaitForDisplayLabelChangeAsync(
                            context.Page, SelectorService, currentTimestamp.Value, context, cancellationToken: cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        await Task.Delay(500, cancellationToken);
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
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Step {Step} cancelled", Name);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Step {Step} failed", Name);
            try
            {
                await BoundedBrowserOperation.RunAsync(
                    SaveErrorDebugAsync(context, $"Failed to capture frames: {ex.Message}", cancellationToken),
                    ErrorDebugSaveTimeout,
                    CancellationToken.None,
                    (message, debugEx) => Logger.LogDebug(debugEx, "Step {Step}: {Message}", Name, message),
                    "save-error-debug").ConfigureAwait(false);
            }
            catch (Exception debugEx)
            {
                Logger.LogDebug(debugEx, "Step {Step}: failed to save error debug artefacts", Name);
            }

            return ScrapingStepResult.Failed($"Failed to capture frames: {ex.Message}");
        }
    }
    
    private async Task DismissModalOverlaysAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasModal = await page.EvaluateAsync<bool>(JavaScriptTemplates.CheckModalOverlay);
            
            if (!hasModal)
            {
                return;
            }
            
            Logger.LogDebug("Modal overlay detected, dismissing");
            
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(200, cancellationToken);
            
            var stillVisible = await page.EvaluateAsync<bool>(JavaScriptTemplates.CheckModalStillVisible);
            
            if (stillVisible)
            {
                try
                {
                    var mapContainer = SelectorService.GetLocator(page, Selectors.MapContainer);
                    await mapContainer.ClickAsync(new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = Selectors.MapContainer.TimeoutMs
                    });
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Ignore if click fails
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error dismissing modal overlay, continuing");
        }
    }
    
    private async Task CaptureMapScreenshotAsync(
        IPage page,
        ILocator mapContainer,
        string outputPath,
        Clip containerClip,
        CancellationToken cancellationToken)
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
            catch (OperationCanceledException)
            {
                throw;
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
        
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Continue if network idle timeout
        }
        
        await DismissModalOverlaysAsync(page, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            Clip = cropArea,
            Type = ScreenshotType.Png,
            Animations = ScreenshotAnimations.Disabled,
            Timeout = ScreenshotTimeoutMs
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

