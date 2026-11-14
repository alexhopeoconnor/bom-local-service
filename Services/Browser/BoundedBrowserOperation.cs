using System.Diagnostics.CodeAnalysis;

namespace BomLocalService.Services.Browser;

/// <summary>
/// Thrown when a browser setup or cleanup operation does not complete within
/// the configured budget. The underlying operation is observed/reclaimed by
/// <see cref="BoundedBrowserOperation"/>.
/// </summary>
public sealed class BrowserOperationTimeoutException : TimeoutException
{
    public BrowserOperationTimeoutException(string operationName, TimeSpan timeout)
        : base($"Browser operation '{operationName}' did not complete within {timeout.TotalSeconds:F0}s")
    {
        OperationName = operationName;
        Timeout = timeout;
    }

    public string OperationName { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Thrown when Chromium or a browser context could not be prepared.
/// </summary>
public sealed class BrowserSetupException : Exception
{
    public BrowserSetupException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Bounds a Playwright operation without abandoning the underlying task.
/// Cancellation/timeout stops the caller waiting, but a late successful resource
/// is passed to <c>reclaim</c>, and late exceptions are observed through
/// <c>observe</c>. Callers must not adopt a resource when this helper throws.
/// </summary>
public static class BoundedBrowserOperation
{
    public static async Task<T> RunAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<T, Task>? reclaim,
        Action<string, Exception?> observe,
        string operationName,
        TimeSpan? reclaimTimeout = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateBudget(timeout);
        ArgumentNullException.ThrowIfNull(observe);
        var effectiveReclaimTimeout = reclaimTimeout ?? timeout;
        ValidateBudget(effectiveReclaimTimeout);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        T? result = null;
        try
        {
            result = await operation.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!linkedSource.IsCancellationRequested)
        {
            // The operation itself was cancelled; this is not our budget expiring.
            throw;
        }
        catch (OperationCanceledException) when (!operation.IsCompleted)
        {
            ObserveLateCompletion(operation, reclaim, observe, operationName, effectiveReclaimTimeout);
            ThrowCancellation(cancellationToken, operationName, timeout);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (operation.IsCompletedSuccessfully)
            {
                await ReclaimAsync(
                    operation.Result,
                    reclaim,
                    observe,
                    operationName,
                    effectiveReclaimTimeout).ConfigureAwait(false);
            }
            else if (operation.IsFaulted)
            {
                ObserveFailure(operation.Exception, observe, operationName);
            }

            ThrowCancellation(cancellationToken, operationName, timeout);
            throw;
        }

        if (linkedSource.IsCancellationRequested)
        {
            // The operation completed, but the caller is no longer entitled to
            // adopt the resource. Reclaim it exactly once, then report the
            // cancellation outside the catch scope above.
            await ReclaimAsync(
                result!,
                reclaim,
                observe,
                operationName,
                effectiveReclaimTimeout).ConfigureAwait(false);
            ThrowCancellation(cancellationToken, operationName, timeout);
        }

        return result!;
    }

    public static async Task RunAsync(
        Task operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string, Exception?> observe,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateBudget(timeout);
        ArgumentNullException.ThrowIfNull(observe);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await operation.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!linkedSource.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!operation.IsCompleted)
        {
            ObserveLateCompletion(operation, observe, operationName);
            ThrowCancellation(cancellationToken, operationName, timeout);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (operation.IsFaulted)
            {
                ObserveFailure(operation.Exception, observe, operationName);
            }

            ThrowCancellation(cancellationToken, operationName, timeout);
            throw;
        }

        if (linkedSource.IsCancellationRequested)
        {
            ThrowCancellation(cancellationToken, operationName, timeout);
        }
    }

    private static void ValidateBudget(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Browser operation budget must be positive.");
        }
    }

    private static async Task ReclaimAsync<T>(
        T resource,
        Func<T, Task>? reclaim,
        Action<string, Exception?> observe,
        string operationName,
        TimeSpan reclaimTimeout)
        where T : class
    {
        if (reclaim is null)
        {
            return;
        }

        // Reclaim may be synchronous and may block (for example
        // IPlaywright.Dispose). Start it on the pool so the caller's budget can
        // still expire, and observe the late task if it outlives that budget.
        Func<Task> reclaimOperation = () => reclaim(resource);
        var reclaimTask = Task.Run(reclaimOperation);

        using var timeoutSource = new CancellationTokenSource(reclaimTimeout);
        try
        {
            await reclaimTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!reclaimTask.IsCompleted)
        {
            var timeoutException = new BrowserOperationTimeoutException(
                $"reclaim {operationName}",
                reclaimTimeout);
            SafeObserve(
                observe,
                $"Reclaiming a late resource for '{operationName}' exceeded the {reclaimTimeout.TotalSeconds:F0}s budget",
                timeoutException);
            ObserveLateCompletion(reclaimTask, observe, $"reclaim {operationName}");
        }
        catch (Exception ex)
        {
            if (reclaimTask.IsFaulted)
            {
                // Accessing Exception also observes a fault that raced the
                // timeout, so it cannot surface as an unobserved task exception.
                SafeObserve(
                    observe,
                    $"Reclaiming a late resource for '{operationName}' failed",
                    reclaimTask.Exception);
            }
            else if (!reclaimTask.IsCompletedSuccessfully)
            {
                SafeObserve(observe, $"Reclaiming a late resource for '{operationName}' failed", ex);
            }
        }
    }

    private static void ObserveLateCompletion<T>(
        Task<T> operation,
        Func<T, Task>? reclaim,
        Action<string, Exception?> observe,
        string operationName,
        TimeSpan reclaimTimeout)
        where T : class
    {
        _ = ObserveLateCompletionAsync(operation, reclaim, observe, operationName, reclaimTimeout);
    }

    private static async Task ObserveLateCompletionAsync<T>(
        Task<T> operation,
        Func<T, Task>? reclaim,
        Action<string, Exception?> observe,
        string operationName,
        TimeSpan reclaimTimeout)
        where T : class
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            await ReclaimAsync(result, reclaim, observe, operationName, reclaimTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeObserve(observe, $"Late completion of '{operationName}' failed", ex);
        }
    }

    private static void ObserveLateCompletion(
        Task operation,
        Action<string, Exception?> observe,
        string operationName)
    {
        _ = ObserveLateCompletionAsync(operation, observe, operationName);
    }

    private static async Task ObserveLateCompletionAsync(
        Task operation,
        Action<string, Exception?> observe,
        string operationName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeObserve(observe, $"Late completion of '{operationName}' failed", ex);
        }
    }

    private static void ObserveFailure(
        AggregateException exception,
        Action<string, Exception?> observe,
        string operationName)
    {
        SafeObserve(observe, $"Bounded operation '{operationName}' failed while cancellation was pending", exception);
    }

    [DoesNotReturn]
    private static void ThrowCancellation(
        CancellationToken cancellationToken,
        string operationName,
        TimeSpan timeout)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"Browser operation '{operationName}' was cancelled",
                cancellationToken);
        }

        throw new BrowserOperationTimeoutException(operationName, timeout);
    }

    private static void SafeObserve(
        Action<string, Exception?> observe,
        string message,
        Exception? exception)
    {
        try
        {
            observe(message, exception);
        }
        catch
        {
            // Observation is diagnostic only. Never surface an observer failure as
            // an unobserved task exception.
        }
    }
}
