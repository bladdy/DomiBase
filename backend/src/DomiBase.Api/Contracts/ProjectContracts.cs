using DomiBase.Application.Projects;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Runtime;

namespace DomiBase.Api.Contracts;

public sealed record CreateProjectRequest(string? Slug, string? Name);

/// <summary>Explicit intent for the idempotent, irreversible project deletion.</summary>
public sealed record DeleteProjectRequest(string? ConfirmSlug);

public sealed record ProjectResponse(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string DatabaseName,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? RateLimitPermits,
    int? RateLimitWindowSeconds,
    int EffectiveRateLimitPermits,
    int EffectiveRateLimitWindowSeconds)
{
    public static ProjectResponse From(Project project, RuntimeOptions options) => new(
        project.Id,
        project.Slug,
        project.Name,
        project.Status.ToString(),
        project.DatabaseName,
        project.FailureReason,
        project.CreatedAt,
        project.UpdatedAt,
        project.RateLimitPermits,
        project.RateLimitWindowSeconds,
        project.RateLimitPermits ?? options.RateLimitPermits,
        project.RateLimitWindowSeconds ?? options.RateLimitWindowSeconds);
}

/// <summary>Null values clear the override and restore the platform default.</summary>
public sealed record UpdateRateLimitRequest(int? Permits, int? WindowSeconds);

public sealed record CreateApiKeyRequest(string? Kind, string? Label, DateTimeOffset? ExpiresAt);

/// <summary>
/// Overlap in minutes during which the previous key value still resolves. Omitted means
/// <see cref="ProjectService.DefaultRotationOverlapMinutes"/>. Zero revokes it immediately.
/// </summary>
public sealed record RotateApiKeyRequest(int? OverlapMinutes);

/// <summary>Sanitized key metadata. Never contains the key value or its hash.</summary>
public sealed record ApiKeyResponse(
    Guid Id,
    Guid ProjectId,
    string Kind,
    string Prefix,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? OverlapExpiresAt)
{
    public static ApiKeyResponse From(ApiKey apiKey) => new(
        apiKey.Id,
        apiKey.ProjectId,
        apiKey.Kind.ToString(),
        apiKey.Prefix,
        apiKey.Label,
        apiKey.CreatedAt,
        apiKey.RevokedAt,
        apiKey.LastUsedAt,
        apiKey.ExpiresAt,
        apiKey.PreviousHashExpiresAt);
}

/// <summary>Response returned only once, at key creation: includes the full key value.</summary>
public sealed record CreatedApiKeyResponse(
    Guid Id,
    Guid ProjectId,
    string Kind,
    string Prefix,
    string Key,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    public static CreatedApiKeyResponse From(CreatedApiKey created) => new(
        created.Key.Id,
        created.Key.ProjectId,
        created.Key.Kind.ToString(),
        created.Key.Prefix,
        created.Value,
        created.Key.Label,
        created.Key.CreatedAt,
        created.Key.ExpiresAt);
}
