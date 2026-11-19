using DomiBase.Application.Auth;

namespace DomiBase.Application.Runtime;

/// <summary>
/// Server-side resolution of a public API key. The client never supplies the project,
/// database, functional role or credentials.
/// </summary>
public sealed record RuntimeProjectContext(
    Guid ProjectId,
    Guid ApiKeyId,
    string ProjectSlug,
    string DatabaseName,
    string RuntimeUsername,
    string RuntimePassword,
    string AnonymousRole,
    string AuthenticatedRole,
    int RateLimitPermits,
    int RateLimitWindowSeconds);

public enum RuntimeResolveStatus
{
    Success = 0,

    /// <summary>The key is missing, malformed, revoked, not a public key, or unknown.</summary>
    InvalidKey = 1,

    /// <summary>The project exists but is not in <c>Ready</c> state.</summary>
    ProjectNotReady = 2,

    /// <summary>The project is ready but its runtime credentials cannot be used (configuration error).</summary>
    ProjectUnavailable = 3,

    /// <summary>Administrative preview only: the requested project does not exist.</summary>
    ProjectNotFound = 4
}

public sealed record RuntimeResolveResult(RuntimeResolveStatus Status, RuntimeProjectContext? Context)
{
    public static RuntimeResolveResult InvalidKey() => new(RuntimeResolveStatus.InvalidKey, null);

    public static RuntimeResolveResult NotReady() => new(RuntimeResolveStatus.ProjectNotReady, null);

    public static RuntimeResolveResult Unavailable() => new(RuntimeResolveStatus.ProjectUnavailable, null);

    public static RuntimeResolveResult NotFound() => new(RuntimeResolveStatus.ProjectNotFound, null);

    public static RuntimeResolveResult Success(RuntimeProjectContext context) =>
        new(RuntimeResolveStatus.Success, context);
}

/// <summary>
/// Resolves an API key to its project and runtime connection context. Implementations must
/// compare hashes only and must never return clear-text key material.
/// </summary>
public interface IRuntimeProjectResolver
{
    Task<RuntimeResolveResult> ResolveAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the same Runtime context from a trusted project id for administrative previews.
    /// No API key is involved and the resulting context carries no key usage telemetry.
    /// </summary>
    Task<RuntimeResolveResult> ResolveForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Records key usage at a coarse interval. Never throws for a lost update.</summary>
    Task TouchAsync(Guid apiKeyId, CancellationToken cancellationToken = default);
}

/// <summary>Rows plus the number of rows affected/returned.</summary>
public sealed record RuntimeResultSet(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Affected);

/// <summary>
/// Executes validated Runtime operations against a project database inside a short
/// transaction with the functional role and request claims applied.
/// </summary>
public interface IRuntimeDataService
{
    Task<RuntimeResultSet> SelectAsync(
        RuntimeProjectContext context,
        string table,
        RuntimeReadOptions options,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default);

    Task<RuntimeResultSet> InsertAsync(
        RuntimeProjectContext context,
        string table,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyList<string>? returning,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default);

    Task<RuntimeResultSet> UpdateAsync(
        RuntimeProjectContext context,
        string table,
        IReadOnlyDictionary<string, string?> values,
        RuntimeWriteOptions options,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default);

    Task<RuntimeResultSet> DeleteAsync(
        RuntimeProjectContext context,
        string table,
        RuntimeWriteOptions options,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default);
}
