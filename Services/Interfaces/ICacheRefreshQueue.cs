using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

public enum RefreshRequestSource
{
    Api,
    Background,
    Manual
}

public enum RefreshRequestResultCode
{
    Accepted,
    AlreadyInProgress,
    RejectedBackoff,
    RejectedQueueFull,
    RejectedShuttingDown,
    RejectedConflict
}

public sealed record RefreshRequestResult(
    RefreshRequestResultCode Code,
    bool Accepted,
    string? Message,
    DateTime? NextRetryAt,
    int QueueDepth)
{
    public bool IsTransientRejection => Code is RefreshRequestResultCode.RejectedBackoff
        or RefreshRequestResultCode.RejectedQueueFull
        or RefreshRequestResultCode.RejectedShuttingDown
        or RefreshRequestResultCode.RejectedConflict;
}

public sealed record RefreshQueueStatus(
    bool IsQueuedOrRunning,
    bool IsRunning,
    bool IsDeleting,
    int QueueDepth,
    int ActiveCaptures);

/// <summary>
/// Single owner of background radar refresh work. Implementations use a bounded
/// queue, deduplicate by location and keep reservations until completion.
/// </summary>
public interface ICacheRefreshQueue
{
    bool IsRunning { get; }
    int QueueDepth { get; }
    RefreshQueueStatus GetStatus(LocationId location);
    bool TryRequestRefresh(LocationId location, RefreshRequestSource source, out RefreshRequestResult result);
    bool TryBeginDelete(LocationId location, out string? error);
    void EndDelete(LocationId location);
    bool IsLocationActive(LocationId location);
}
