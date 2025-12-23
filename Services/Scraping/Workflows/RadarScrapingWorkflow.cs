using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;
using BomLocalService.Utilities;

namespace BomLocalService.Services.Scraping.Workflows;

public class RadarScrapingWorkflow : IRadarScrapingWorkflow
{
    private readonly ILogger<RadarScrapingWorkflow> _logger;
    private readonly IScrapingStepRegistry _stepRegistry;
    private readonly IConfiguration _configuration;
    private readonly ICacheService _cacheService;
    private readonly double _cacheExpirationMinutes;
    private readonly int _cacheManagementCheckIntervalMinutes;
    
    public string Name => "RadarScraping";
    public string Description => "Scrapes radar images for a location";
    
    // Fixed step sequence - order matters due to dependencies
    public string[] StepNames => new[]
    {
        "NavigateHomepage",
        "ClickSearchButton",
        "FillSearchInput",
        "WaitForSearchResults",
        "SelectSearchResult",
        "ClickRadarLink",
        "WaitForMapReady",
        "PauseRadar",
        "ResetToFirstFrame",
        "ExtractMetadata",
        "CalculateMapBounds",
        "CaptureFrames"
    };
    
    public RadarScrapingWorkflow(
        ILogger<RadarScrapingWorkflow> logger,
        IScrapingStepRegistry stepRegistry,
        IConfiguration configuration,
        ICacheService cacheService)
    {
        _logger = logger;
        _stepRegistry = stepRegistry;
        _configuration = configuration;
        _cacheService = cacheService;
        
        var cacheExpirationMinutesConfig = configuration.GetValue<double?>("CacheExpirationMinutes");
        if (!cacheExpirationMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");
        }
        _cacheExpirationMinutes = cacheExpirationMinutesConfig.Value;
        
        var cacheManagementCheckIntervalMinutesConfig = configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes");
        if (!cacheManagementCheckIntervalMinutesConfig.HasValue)
        {
            throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
        }
        _cacheManagementCheckIntervalMinutes = cacheManagementCheckIntervalMinutesConfig.Value;
    }
    
    public async Task<RadarResponse> ExecuteAsync(ScrapingContext context, CancellationToken cancellationToken)
    {
        var workflowConfig = _configuration.GetSection($"Scraping:Workflows:{Name}").Get<ScrapingWorkflowConfig>();
        var workflowStartTime = DateTime.UtcNow;
        var stepTimings = new List<(string stepName, double durationSeconds)>();
        
        // Record Initializing phase start for metrics tracking
        if (!string.IsNullOrEmpty(context.CacheFolderPath))
        {
            _cacheService.RecordUpdateProgressByFolder(context.CacheFolderPath, CacheUpdatePhase.Initializing);
        }
        
        foreach (var stepName in StepNames)
        {
            var stepConfig = workflowConfig?.Steps?.GetValueOrDefault(stepName) ?? new ScrapingStepConfig { Enabled = true };
            
            if (!stepConfig.Enabled)
            {
                _logger.LogInformation("Step {Step} is disabled, skipping", stepName);
                continue;
            }
            
            var step = _stepRegistry.GetStep(stepName);
            if (step == null)
            {
                throw new InvalidOperationException($"Step {stepName} not found in registry");
            }
            
            if (!ValidatePrerequisites(step, context))
            {
                throw new InvalidOperationException(
                    $"Step {stepName} prerequisites not met. Required: {string.Join(", ", step.Prerequisites)}");
            }
            
            if (!step.CanExecute(context))
            {
                throw new InvalidOperationException(
                    $"Step {stepName} cannot execute in current page state: {context.CurrentState}");
            }
            
            var stepStartTime = DateTime.UtcNow;
            _logger.LogInformation("Executing step {Step}", stepName);
            
            var result = await step.ExecuteAsync(context, cancellationToken);
            
            var stepDuration = (DateTime.UtcNow - stepStartTime).TotalSeconds;
            stepTimings.Add((stepName, stepDuration));
            
            // Record step timing in metrics
            _cacheService.RecordStepCompletion(stepName, stepDuration);
            
            // Get historical average for comparison
            var avgDuration = _cacheService.GetAverageStepDuration(stepName);
            if (avgDuration > 0)
            {
                var diff = stepDuration - avgDuration;
                var diffPercent = (diff / avgDuration) * 100;
                var isSlow = stepDuration > avgDuration * 1.5; // 50% slower than average
                
                if (isSlow)
                {
                    _logger.LogWarning("Step {Step} took significantly longer than average: {Duration:F2}s (avg: {Avg:F2}s, {Diff:+#.##}s, {DiffPercent:+#0.#}% slower)",
                        stepName, stepDuration, avgDuration, diff, diffPercent);
                }
                else if (Math.Abs(diffPercent) > 20) // More than 20% difference (faster or slower)
                {
                    _logger.LogInformation("Step {Step} completed in {Duration:F2}s (avg: {Avg:F2}s, {Diff:+#.##;-#.##}s, {DiffPercent:+#0.#;-#0.#}%)",
                        stepName, stepDuration, avgDuration, diff, diffPercent);
                }
                else
                {
                    _logger.LogInformation("Step {Step} completed in {Duration:F2}s (avg: {Avg:F2}s)", stepName, stepDuration, avgDuration);
                }
            }
            else
            {
                _logger.LogInformation("Step {Step} completed in {Duration:F2}s", stepName, stepDuration);
            }
            
            if (!result.Success)
            {
                throw new Exception($"Step {stepName} failed: {result.ErrorMessage}");
            }
            
            context.CompletedSteps.Add(stepName);
        }
        
        var totalDuration = (DateTime.UtcNow - workflowStartTime).TotalSeconds;
        
        // Check if workflow duration is significantly longer than average
        var avgTotalDuration = _cacheService.GetAverageTotalDuration();
        var isWorkflowSlow = avgTotalDuration > 0 && totalDuration > avgTotalDuration * 1.3; // 30% slower than average
        
        // Log step breakdown
        var stepBreakdown = string.Join(", ", stepTimings.Select(t => $"{t.stepName}={t.durationSeconds:F2}s"));
        
        if (isWorkflowSlow)
        {
            var workflowDiff = totalDuration - avgTotalDuration;
            var workflowDiffPercent = (workflowDiff / avgTotalDuration) * 100;
            _logger.LogWarning("Workflow {Workflow} took significantly longer than average: {TotalDuration:F2}s (avg: {Avg:F2}s, {Diff:+#.##}s, {DiffPercent:+#0.#}% slower) for {Suburb}, {State}",
                Name, totalDuration, avgTotalDuration, workflowDiff, workflowDiffPercent, context.Suburb, context.State);
        }
        else
        {
            _logger.LogInformation("Workflow {Workflow} completed in {TotalDuration:F2}s for {Suburb}, {State}. Step breakdown: {StepBreakdown}",
                Name, totalDuration, context.Suburb, context.State, stepBreakdown);
        }
        
        // Log step performance summary if we have historical data
        var stepMetrics = _cacheService.GetStepMetrics();
        if (stepMetrics.Count > 0)
        {
            var slowSteps = stepTimings
                .Where(t => stepMetrics.ContainsKey(t.stepName) && t.durationSeconds > stepMetrics[t.stepName] * 1.5)
                .Select(t => $"{t.stepName} ({t.durationSeconds:F2}s vs avg {stepMetrics[t.stepName]:F2}s)")
                .ToList();
            
            if (slowSteps.Any())
            {
                _logger.LogWarning("Workflow {Workflow} had slower-than-average steps: {SlowSteps}",
                    Name, string.Join(", ", slowSteps));
            }
        }
        
        return BuildResponse(context);
    }
    
    private bool ValidatePrerequisites(IScrapingStep step, ScrapingContext context)
    {
        return step.Prerequisites.All(prereq => context.CompletedSteps.Contains(prereq));
    }
    
    private RadarResponse BuildResponse(ScrapingContext context)
    {
        if (context.LastUpdatedInfo == null)
        {
            throw new InvalidOperationException("LastUpdatedInfo is required to build response");
        }
        
        if (context.Frames == null || context.Frames.Count == 0)
        {
            throw new InvalidOperationException("Frames are required to build response");
        }
        
        var cacheExpiresAt = context.LastUpdatedInfo.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        
        return ResponseBuilder.CreateRadarResponse(
            context.CacheFolderPath,
            context.Frames,
            _cacheManagementCheckIntervalMinutes,
            context.LastUpdatedInfo,
            context.Suburb,
            context.State,
            cacheIsValid: true,
            cacheExpiresAt: cacheExpiresAt,
            isUpdating: false);
    }
}

