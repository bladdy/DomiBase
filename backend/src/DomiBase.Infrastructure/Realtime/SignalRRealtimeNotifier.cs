using DomiBase.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Realtime;

/// <summary>
/// Default SignalR notifier. Publishes to the group <c>project:{projectId}:table:{table}</c>.
/// Failures are logged and swallowed; the originating HTTP request has already committed.
/// </summary>
public sealed class SignalRRealtimeNotifier(
    IHubContext<RealtimeHub> hub,
    ILogger<SignalRRealtimeNotifier> logger) : IRealtimeNotifier
{
    public async Task NotifyAsync(
        Guid projectId,
        RealtimeEvent eventPayload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var group = GroupName(projectId, eventPayload.Table);
            await hub.Clients.Group(group).SendAsync(
                "change",
                eventPayload,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Notification is best-effort after commit; do not fail the HTTP response.
            logger.LogWarning(
                exception,
                "Failed to send realtime notification for project {ProjectId}, table {Table}.",
                projectId,
                eventPayload.Table);
        }
    }

    public static string GroupName(Guid projectId, string table) =>
        $"project:{projectId}:table:{table}";
}
