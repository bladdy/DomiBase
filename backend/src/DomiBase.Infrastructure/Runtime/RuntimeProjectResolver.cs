using DomiBase.Application.ApiKeys;
using DomiBase.Application.Runtime;
using DomiBase.Application.Security;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Runtime;

/// <summary>
/// Resolves a public API key to its project. Only the HMAC hash is compared, revoked keys
/// are rejected, and the runtime password is decrypted in memory for the connection only.
/// </summary>
public sealed class RuntimeProjectResolver(
    DomiBaseDbContext db,
    IApiKeyGenerator apiKeys,
    ISecretProtector secrets,
    RuntimeOptions options,
    ILogger<RuntimeProjectResolver> logger) : IRuntimeProjectResolver
{
    private const string PublicKeyPrefix = "db_public_";
    private const int MaxKeyLength = 512;
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    public async Task<RuntimeResolveResult> ResolveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Length > MaxKeyLength ||
            !apiKey.StartsWith(PublicKeyPrefix, StringComparison.Ordinal))
        {
            return RuntimeResolveResult.InvalidKey();
        }

        // During pepper rotation a stored hash may have been produced by any pepper still
        // in the keyring. All candidates are computed server-side and matched in SQL.
        var hashes = apiKeys.ComputeVerificationHashes(apiKey);
        var now = DateTimeOffset.UtcNow;

        // A current key must be active, unexpired and public. Secret keys are deliberately
        // excluded here: they never grant Runtime access or an elevated PostgreSQL role.
        var key = await db.ApiKeys
            .AsNoTracking()
            .Include(k => k.Project)
            .FirstOrDefaultAsync(
                k => hashes.Contains(k.SecretHash) &&
                     k.Kind == ApiKeyKind.Public &&
                     k.RevokedAt == null &&
                     (k.ExpiresAt == null || k.ExpiresAt > now),
                cancellationToken);

        // During a rotation overlap the previous value resolves to the replacement row.
        // The old row is already revoked, so it can never match the query above.
        if (key is null)
        {
            key = await db.ApiKeys
                .AsNoTracking()
                .Include(k => k.Project)
                .FirstOrDefaultAsync(
                    k => hashes.Contains(k.PreviousSecretHash) &&
                         k.PreviousHashExpiresAt > now &&
                         k.Kind == ApiKeyKind.Public &&
                         k.RevokedAt == null &&
                         (k.ExpiresAt == null || k.ExpiresAt > now),
                    cancellationToken);
        }

        if (key is null || key.Kind != ApiKeyKind.Public || key.IsRevoked || key.Project is null)
        {
            return RuntimeResolveResult.InvalidKey();
        }

        return BuildContext(key.Project, key.Id);
    }

    public async Task<RuntimeResolveResult> ResolveForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        return project is null
            ? RuntimeResolveResult.NotFound()
            : BuildContext(project, Guid.Empty);
    }

    private RuntimeResolveResult BuildContext(Project project, Guid apiKeyId)
    {
        if (project.Status != ProjectStatus.Ready)
        {
            return RuntimeResolveResult.NotReady();
        }

        if (project.RuntimePasswordCiphertext is null ||
            !secrets.TryUnprotect(project.RuntimePasswordCiphertext, out var password) ||
            string.IsNullOrWhiteSpace(password))
        {
            // Key material changed, row damaged, or provisioning never stored credentials.
            logger.LogWarning(
                "Project {ProjectId} has no usable runtime credentials.",
                project.Id);
            return RuntimeResolveResult.Unavailable();
        }

        var context = new RuntimeProjectContext(
            ProjectId: project.Id,
            ApiKeyId: apiKeyId,
            ProjectSlug: project.Slug,
            DatabaseName: project.DatabaseName,
            RuntimeUsername: ProjectNaming.ToRuntimeRole(project.Slug),
            RuntimePassword: password,
            AnonymousRole: ProjectNaming.ToAnonymousRole(project.Slug),
            AuthenticatedRole: ProjectNaming.ToAuthenticatedRole(project.Slug),
            RateLimitPermits: project.RateLimitPermits ?? options.RateLimitPermits,
            RateLimitWindowSeconds: project.RateLimitWindowSeconds ?? options.RateLimitWindowSeconds);

        return RuntimeResolveResult.Success(context);
    }

    public async Task TouchAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - LastUsedResolution;

            await db.ApiKeys
                .Where(k => k.Id == apiKeyId && (k.LastUsedAt == null || k.LastUsedAt < cutoff))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(k => k.LastUsedAt, now),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Usage telemetry must never break a data request.
            logger.LogDebug(exception, "Could not update last_used_at for API key {ApiKeyId}.", apiKeyId);
        }
    }
}
