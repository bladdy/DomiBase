namespace DomiBase.Domain.Projects;

/// <summary>
/// Shared validation rules for per-project Runtime rate limits. A null override inherits
/// the platform-wide default; the Runtime resolves the effective pair for every request.
/// </summary>
public static class ProjectRateLimits
{
    public const int MinPermits = 1;
    public const int MaxPermits = 10_000;
    public const int MinWindowSeconds = 1;
    public const int MaxWindowSeconds = 3_600;

    public static bool IsValidPermits(int? value) =>
        value is null || (value >= MinPermits && value <= MaxPermits);

    public static bool IsValidWindowSeconds(int? value) =>
        value is null || (value >= MinWindowSeconds && value <= MaxWindowSeconds);

    public static bool TryValidate(int? permits, int? windowSeconds, out string? error)
    {
        if (!IsValidPermits(permits))
        {
            error = $"rateLimitPermits must be between {MinPermits} and {MaxPermits}, or null.";
            return false;
        }

        if (!IsValidWindowSeconds(windowSeconds))
        {
            error = $"rateLimitWindowSeconds must be between {MinWindowSeconds} and {MaxWindowSeconds}, or null.";
            return false;
        }

        error = null;
        return true;
    }
}
