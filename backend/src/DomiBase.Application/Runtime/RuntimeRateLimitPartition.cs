namespace DomiBase.Application.Runtime;

/// <summary>
/// Partition keys for the Runtime fixed-window limiter. Keys never contain API key values,
/// project names or connection details. Effective limits are included so changing an override
/// starts a new window instead of inheriting the previous quota.
/// </summary>
public static class RuntimeRateLimitPartition
{
    public static string ForProject(Guid projectId, int permits, int windowSeconds) =>
        $"project:{projectId:D}:{permits}:{windowSeconds}";

    public static string ForAddress(string? remoteIpAddress) =>
        $"ip:{remoteIpAddress ?? "unknown"}";
}
