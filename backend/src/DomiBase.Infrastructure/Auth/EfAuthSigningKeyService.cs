using DomiBase.Application.Auth;
using DomiBase.Application.Security;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Auth;

/// <summary>
/// EF Core implementation of the per-project signing key store. The PKCS#8 private key is
/// only persisted encrypted (AES-256-GCM); concurrent creators converge on the unique
/// partial index <c>(project_id) WHERE retired_at IS NULL</c>.
/// </summary>
public sealed class EfAuthSigningKeyService(
    DomiBaseDbContext db,
    ISecretProtector secrets,
    ILogger<EfAuthSigningKeyService> logger) : IAuthSigningKeyService
{
    public async Task<AuthSigningKeyMaterial?> FindActiveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var key = await db.ProjectAuthKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.ProjectId == projectId && k.RetiredAt == null, cancellationToken);

        return key is null ? null : ToMaterial(key);
    }

    public async Task<AuthSigningKeyMaterial> GetOrCreateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindActiveAsync(projectId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var pair = AuthKeyFactory.Generate();
        var entity = new ProjectAuthKey
        {
            ProjectId = projectId,
            KeyId = pair.KeyId,
            PublicKey = pair.PublicKeySpkiBase64,
            PrivateKeyCiphertext = secrets.Protect(pair.PrivateKeyPkcs8Base64)
        };

        db.ProjectAuthKeys.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Another request created the active key concurrently: use that one instead.
            db.Entry(entity).State = EntityState.Detached;
            logger.LogDebug(
                exception,
                "Concurrent signing key creation for project {ProjectId}; reusing the stored key.",
                projectId);

            var concurrent = await FindActiveAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException("The project signing key could not be created.");

            return concurrent;
        }

        logger.LogInformation("Created ES256 signing key {KeyId} for project {ProjectId}.", pair.KeyId, projectId);

        return new AuthSigningKeyMaterial(pair.KeyId, pair.PublicKeySpkiBase64, pair.PrivateKeyPkcs8Base64);
    }

    private AuthSigningKeyMaterial ToMaterial(ProjectAuthKey key)
    {
        if (!secrets.TryUnprotect(key.PrivateKeyCiphertext, out var privateKey) ||
            string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException(
                "The project signing key could not be decrypted with the configured secrets key.");
        }

        return new AuthSigningKeyMaterial(key.KeyId, key.PublicKey, privateKey);
    }
}
