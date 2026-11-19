namespace DomiBase.Application.Realtime;

/// <summary>
/// A change notification delivered after the database transaction commits.
/// The payload is intentionally minimal; clients refetch through the Runtime API
/// so RLS policies remain authoritative.
/// </summary>
public sealed record RealtimeEvent(
    string Table,
    string Operation,
    string? RowId,
    DateTimeOffset Timestamp);

/// <summary>
/// Publishes post-commit data change events. Implementations must not throw when
/// the notifier is unavailable; callers already committed the transaction.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Notifies subscribers of a change in a project table. Implementations should
    /// swallow non-critical exceptions so a notification failure does not break the
    /// HTTP response.
    /// </summary>
    Task NotifyAsync(
        Guid projectId,
        RealtimeEvent eventPayload,
        CancellationToken cancellationToken = default);
}
