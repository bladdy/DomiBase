using System.Text.Json;
using DomiBase.Application.ApiKeys;
using Microsoft.Extensions.Logging;
using DomiBase.Application.Auth;
using DomiBase.Application.Security;
using DomiBase.Application.Storage;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.Projects;

public sealed record CreatedApiKey(ApiKey Key, string Value);

/// <summary>
/// Control Plane orchestration: project lifecycle, key lifecycle and audit.
/// Runtime data access is deliberately absent from this service.
/// </summary>
public sealed class ProjectService(
    IProjectRepository repository,
    IProjectProvisioner provisioner,
    IApiKeyGenerator apiKeys,
    ISecretProtector secrets,
    IAuthSigningKeyService authSigningKeys,
    IStorageRepository storage,
    IStorageBackend storageBackend,
    ILogger<ProjectService> logger)
{
    public const string AdminActor = "admin";
    public const string DeleteConfirmationHeader = "X-DomiBase-Confirm-Delete";
    public const string DeleteConfirmationValue = "delete";
    private const int MaxNameLength = 120;
    private const int MaxLabelLength = 120;
    private const int MaxFailureReasonLength = 1000;
    public const int DefaultRotationOverlapMinutes = 60;
    public const int MaxRotationOverlapMinutes = 1440;

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        repository.ListProjectsAsync(cancellationToken);

    public Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.FindProjectAsync(id, cancellationToken);

    public async Task<ControlResult<Project>> CreateAsync(
        string? slugInput,
        string? name,
        CancellationToken cancellationToken = default)
    {
        if (!SlugValidator.TryNormalize(slugInput, out var slug, out var slugError))
        {
            return ControlResult<Project>.Fail(ControlErrorCode.Validation, slugError!);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return ControlResult<Project>.Fail(ControlErrorCode.Validation, "Project name is required.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength)
        {
            return ControlResult<Project>.Fail(
                ControlErrorCode.Validation,
                $"Project name must be at most {MaxNameLength} characters.");
        }

        if (await repository.SlugExistsAsync(slug, cancellationToken))
        {
            return ControlResult<Project>.Fail(
                ControlErrorCode.Conflict,
                $"A project with slug '{slug}' already exists.");
        }

        var project = new Project
        {
            Slug = slug,
            Name = trimmedName,
            DatabaseName = ProjectNaming.ToDatabaseName(slug),
            Status = ProjectStatus.Provisioning
        };

        var runtimeCredentials = GetOrCreateRuntimeCredentials(project);

        if (!await repository.TryAddProjectAsync(project, cancellationToken))
        {
            return ControlResult<Project>.Fail(
                ControlErrorCode.Conflict,
                $"A project with slug '{slug}' already exists.");
        }

        await repository.AddAuditAsync(
            Audit("project.create", project.Id, project.Id, new { project.Slug, project.DatabaseName }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return await ProvisionAsync(project, runtimeCredentials);
    }

    public async Task<ControlResult<Project>> RetryProvisioningAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound(id);
        }

        return project.Status switch
        {
            ProjectStatus.Provisioning => ControlResult<Project>.Fail(
                ControlErrorCode.Conflict,
                "Provisioning is already in progress."),
            ProjectStatus.Deleting => ControlResult<Project>.Fail(
                ControlErrorCode.Conflict,
                "The project is being deleted."),
            ProjectStatus.Ready when HasUsableRuntimeCredentials(project) => await EnsureAuthAsync(project, cancellationToken),
            _ => await ProvisionAsync(project, GetOrCreateRuntimeCredentials(project))
        };
    }

    /// <summary>
    /// Overrides the Runtime rate limit for one project. Null clears an override so the
    /// platform default applies again. Changes are audited and never logged with key material.
    /// </summary>
    public async Task<ControlResult<Project>> UpdateRateLimitAsync(
        Guid id,
        int? permits,
        int? windowSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!ProjectRateLimits.TryValidate(permits, windowSeconds, out var error))
        {
            return ControlResult<Project>.Fail(ControlErrorCode.Validation, error!);
        }

        var project = await repository.FindProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound(id);
        }

        project.RateLimitPermits = permits;
        project.RateLimitWindowSeconds = windowSeconds;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await repository.AddAuditAsync(
            Audit(
                "project.rate_limit.updated",
                project.Id,
                project.Id,
                new
                {
                    project.Slug,
                    Permits = permits,
                    WindowSeconds = windowSeconds
                }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<Project>.Ok(project);
    }

    /// <summary>
    /// Idempotent project deletion. The administrative token is verified by middleware;
    /// the extra header and exact slug protect against accidental deletion. A repeated call
    /// resumes a partially completed deletion instead of failing.
    /// </summary>
    public async Task<ControlResult<bool>> DeleteAsync(
        Guid id,
        string? confirmSlug,
        string? confirmationHeader,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(confirmationHeader, DeleteConfirmationValue, StringComparison.OrdinalIgnoreCase))
        {
            return ControlResult<bool>.Fail(
                ControlErrorCode.Validation,
                $"Send '{DeleteConfirmationValue}' in the {DeleteConfirmationHeader} header to confirm deletion.");
        }

        if (string.IsNullOrWhiteSpace(confirmSlug))
        {
            return ControlResult<bool>.Fail(
                ControlErrorCode.Validation,
                "The exact project slug is required to confirm deletion.");
        }

        var project = await repository.FindProjectAsync(id, cancellationToken);
        if (project is null)
        {
            // Already deleted: the operation is idempotent and reveals nothing new.
            return ControlResult<bool>.Ok(true);
        }

        if (!string.Equals(project.Slug, confirmSlug.Trim(), StringComparison.Ordinal))
        {
            return ControlResult<bool>.Fail(
                ControlErrorCode.Validation,
                "The confirmation slug does not match the project.");
        }

        var now = DateTimeOffset.UtcNow;
        if (project.Status != ProjectStatus.Deleting)
        {
            project.Status = ProjectStatus.Deleting;
            project.FailureReason = null;
            project.UpdatedAt = now;
            await repository.AddAuditAsync(
                Audit("project.delete_requested", id, id, new { project.Slug, project.DatabaseName }),
                cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await CleanupStorageAsync(project, cancellationToken);

            foreach (var key in await repository.ListApiKeysAsync(id, cancellationToken))
            {
                if (key.RevokedAt is not null)
                {
                    continue;
                }

                key.RevokedAt = now;
                await repository.AddAuditAsync(
                    Audit("api_key.revoke", key.Id, id, new { ProjectId = id, key.Prefix }),
                    cancellationToken);
            }

            project.RuntimePasswordCiphertext = null;
            project.UpdatedAt = now;
            await repository.SaveChangesAsync(cancellationToken);

            var sink = await CreateStepSinkAsync(id, CancellationToken.None);
            var outcome = await provisioner.DeprovisionAsync(project, CancellationToken.None, sink);
            if (!outcome.Succeeded)
            {
                return await FailDeletionAsync(project, outcome.Error, cancellationToken);
            }

            await repository.DeleteProjectMetadataAsync(id, CancellationToken.None);
            await repository.AddAuditAsync(
                Audit("project.deleted", id, id, new { project.Slug, project.DatabaseName }),
                CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);

            return ControlResult<bool>.Ok(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Deletion failed for project {ProjectId}.", id);
            return await FailDeletionAsync(project, exception.Message, CancellationToken.None);
        }
    }

    public async Task<ControlResult<CreatedApiKey>> CreateApiKeyAsync(
        Guid projectId,
        string? kind,
        string? label,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<CreatedApiKey>.Fail(ControlErrorCode.NotFound, $"Project '{projectId}' was not found.");
        }

        if (project.Status != ProjectStatus.Ready)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "API keys can only be created for projects in Ready state.");
        }

        if (!TryParseKind(kind, out var parsedKind, out var kindError))
        {
            return ControlResult<CreatedApiKey>.Fail(ControlErrorCode.Validation, kindError!);
        }

        if (label is not null && label.Trim().Length > MaxLabelLength)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Validation,
                $"Label must be at most {MaxLabelLength} characters.");
        }

        if (!TryValidateExpiry(expiresAt, out var expiryError))
        {
            return ControlResult<CreatedApiKey>.Fail(ControlErrorCode.Validation, expiryError!);
        }

        var generated = apiKeys.Generate(parsedKind);
        var apiKey = new ApiKey
        {
            ProjectId = project.Id,
            Kind = parsedKind,
            Prefix = generated.Prefix,
            SecretHash = generated.SecretHash,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            ExpiresAt = expiresAt
        };

        if (!await repository.TryAddApiKeyAsync(apiKey, cancellationToken))
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "The generated API key conflicted with an existing key. Please retry.");
        }

        await repository.AddAuditAsync(
            Audit("api_key.create", apiKey.Id, project.Id, new { ProjectId = project.Id, apiKey.Kind, apiKey.Prefix }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<CreatedApiKey>.Ok(new CreatedApiKey(apiKey, generated.Value));
    }

    public async Task<ControlResult<IReadOnlyList<ApiKey>>> ListApiKeysAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<IReadOnlyList<ApiKey>>.Fail(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found.");
        }

        var keys = await repository.ListApiKeysAsync(projectId, cancellationToken);
        return ControlResult<IReadOnlyList<ApiKey>>.Ok(keys);
    }

    public async Task<ControlResult<bool>> RevokeApiKeyAsync(
        Guid projectId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<bool>.Fail(ControlErrorCode.NotFound, $"Project '{projectId}' was not found.");
        }

        var apiKey = await repository.FindApiKeyAsync(projectId, keyId, cancellationToken);
        if (apiKey is null)
        {
            return ControlResult<bool>.Fail(ControlErrorCode.NotFound, $"API key '{keyId}' was not found.");
        }

        if (apiKey.RevokedAt is not null)
        {
            return ControlResult<bool>.Ok(true);
        }

        apiKey.RevokedAt = DateTimeOffset.UtcNow;
        await repository.AddAuditAsync(
            Audit("api_key.revoke", apiKey.Id, projectId, new { ProjectId = projectId, apiKey.Prefix }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<bool>.Ok(true);
    }

    /// <summary>
    /// Issues a replacement key of the same kind. The old value keeps working only until
    /// the overlap expires; its row is revoked immediately so it can never outlive that window.
    /// The new full value is returned once and is never written to audit or metadata.
    /// </summary>
    public async Task<ControlResult<CreatedApiKey>> RotateApiKeyAsync(
        Guid projectId,
        Guid keyId,
        int? overlapMinutes,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found.");
        }

        if (project.Status != ProjectStatus.Ready)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "API keys can only be rotated for projects in Ready state.");
        }

        var overlap = overlapMinutes ?? DefaultRotationOverlapMinutes;
        if (overlap < 0 || overlap > MaxRotationOverlapMinutes)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Validation,
                $"Overlap must be between 0 and {MaxRotationOverlapMinutes} minutes.");
        }

        var previous = await repository.FindApiKeyAsync(projectId, keyId, cancellationToken);
        if (previous is null)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.NotFound,
                $"API key '{keyId}' was not found.");
        }

        if (previous.IsRevoked)
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "A revoked API key cannot be rotated.");
        }

        if (previous.IsExpired(DateTimeOffset.UtcNow))
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "An expired API key cannot be rotated.");
        }

        var generated = apiKeys.Generate(previous.Kind);
        var now = DateTimeOffset.UtcNow;
        var replacement = new ApiKey
        {
            ProjectId = projectId,
            Kind = previous.Kind,
            Prefix = generated.Prefix,
            SecretHash = generated.SecretHash,
            Label = previous.Label,
            ExpiresAt = previous.ExpiresAt,
            PreviousSecretHash = overlap > 0 ? previous.SecretHash : null,
            PreviousHashExpiresAt = overlap > 0 ? now.AddMinutes(overlap) : null
        };

        // Calling Generate again on the same kind has negligible collision risk, but a
        // conflict must never revoke the working key. Add first, revoke afterwards.
        if (!await repository.TryAddApiKeyAsync(replacement, cancellationToken))
        {
            return ControlResult<CreatedApiKey>.Fail(
                ControlErrorCode.Conflict,
                "The generated API key conflicted with an existing key. Please retry.");
        }

        previous.RevokedAt = now;
        await repository.AddAuditAsync(
            Audit(
                "api_key.revoke",
                previous.Id,
                projectId,
                new { ProjectId = projectId, previous.Prefix, Reason = "rotate" }),
            cancellationToken);
        await repository.AddAuditAsync(
            Audit(
                "api_key.rotate",
                replacement.Id,
                projectId,
                new
                {
                    ProjectId = projectId,
                    replacement.Kind,
                    replacement.Prefix,
                    PreviousKeyId = previous.Id,
                    overlap
                }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<CreatedApiKey>.Ok(new CreatedApiKey(replacement, generated.Value));
    }

    private async Task<ControlResult<Project>> ProvisionAsync(
        Project project,
        ProjectRuntimeCredentials runtimeCredentials)
    {
        // Provisioning is a committed state transition: once the project row exists, finish it
        // even if the HTTP client disconnects. A half-applied run would leave the project stuck
        // in Provisioning; the retry endpoint relies on the terminal Failed/Ready states.
        var sink = await CreateStepSinkAsync(project.Id, CancellationToken.None);

        var outcome = await provisioner.ProvisionAsync(
            project,
            runtimeCredentials,
            CancellationToken.None,
            sink);
        var completedAt = DateTimeOffset.UtcNow;

        project.Status = outcome.Succeeded ? ProjectStatus.Ready : ProjectStatus.Failed;
        project.FailureReason = outcome.Succeeded ? null : Truncate(outcome.Error, MaxFailureReasonLength);
        project.UpdatedAt = completedAt;

        await repository.SaveChangesAsync(CancellationToken.None);

        await repository.AddAuditAsync(
            Audit(
                outcome.Succeeded ? "project.provisioned" : "project.provision_failed",
                project.Id,
                project.Id,
                new { project.Slug, project.DatabaseName, Status = project.Status.ToString() }),
            CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        if (outcome.Succeeded)
        {
            // The signing key is generated once per project and stored with its private
            // half encrypted; a failed attempt here surfaces as a provisioning failure
            // on the next retry instead of an unusable Auth configuration.
            await authSigningKeys.GetOrCreateAsync(project.Id, CancellationToken.None);
        }
        else if (outcome.Steps.Any(step => step.Succeeded))
        {
            // Only compensate when something was actually created. A lock conflict creates
            // nothing and must not remove resources owned by a concurrent provisioning run.
            var compensation = await provisioner.CompensateAsync(project, CancellationToken.None, sink);
            if (!compensation.Succeeded)
            {
                logger.LogWarning(
                    "Compensation failed for project {ProjectId}: {Reason}",
                    project.Id,
                    compensation.Error);
            }

            await repository.AddAuditAsync(
                Audit(
                    compensation.Succeeded ? "project.compensated" : "project.compensation_failed",
                    project.Id,
                    project.Id,
                    new { project.Slug, Error = Truncate(compensation.Error, MaxFailureReasonLength) }),
                CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        return ControlResult<Project>.Ok(project);
    }

    /// <summary>
    /// Writes each privileged step to <c>domibase_control</c> as it starts and finishes.
    /// The attempt and correlation id are fixed for the whole provisioning run.
    /// </summary>
    private sealed class ProvisioningStepSink(
        IProjectRepository repository,
        Guid projectId,
        int attempt,
        string correlationId) : IProvisioningStepSink
    {
        public async Task<Guid> BeginAsync(string step, CancellationToken cancellationToken = default)
        {
            var operation = new ProvisioningOperation
            {
                ProjectId = projectId,
                Step = step,
                Status = ProvisioningStatus.Running,
                Attempt = attempt,
                CorrelationId = correlationId,
                StartedAt = DateTimeOffset.UtcNow
            };

            await repository.AddProvisioningOperationAsync(operation, cancellationToken);
            return operation.Id;
        }

        public Task CompleteAsync(
            Guid operationId,
            ProvisioningStatus status,
            string? error,
            CancellationToken cancellationToken = default) =>
            repository.CompleteProvisioningOperationAsync(
                operationId,
                status,
                error,
                DateTimeOffset.UtcNow,
                cancellationToken);
    }

    private async Task<ControlResult<bool>> FailDeletionAsync(
        Project project,
        string? reason,
        CancellationToken cancellationToken)
    {
        project.FailureReason = Truncate(reason, MaxFailureReasonLength);
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await repository.AddAuditAsync(
            Audit(
                "project.delete_failed",
                project.Id,
                project.Id,
                new { project.Slug, Error = Truncate(reason, MaxFailureReasonLength) }),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<bool>.Fail(
            ControlErrorCode.Conflict,
            "Deletion could not complete. Retry the same request; cleanup is idempotent.");
    }

    private async Task CleanupStorageAsync(Project project, CancellationToken cancellationToken)
    {
        foreach (var bucket in await storage.ListBucketsAsync(project.Id, cancellationToken))
        {
            foreach (var obj in await storage.ListObjectsAsync(bucket.Id, null, cancellationToken))
            {
                await storageBackend.DeleteAsync(project.Id, bucket.Name, obj.Path, cancellationToken);
            }
        }
    }

    private async Task<ProvisioningStepSink> CreateStepSinkAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var attempt = await repository.NextProvisioningAttemptAsync(projectId, cancellationToken);
        var correlationId = Guid.CreateVersion7().ToString("N");
        return new ProvisioningStepSink(repository, projectId, attempt, correlationId);
    }

    private async Task<ControlResult<Project>> EnsureAuthAsync(Project project, CancellationToken cancellationToken)
    {
        // Projects provisioned before Auth existed are upgraded idempotently on retry.
        await provisioner.EnsureAuthSchemaAsync(project, cancellationToken);
        await authSigningKeys.GetOrCreateAsync(project.Id, cancellationToken);
        return ControlResult<Project>.Ok(project);
    }

    private bool HasUsableRuntimeCredentials(Project project) =>
        project.RuntimePasswordCiphertext is not null &&
        secrets.TryUnprotect(project.RuntimePasswordCiphertext, out _);

    private ProjectRuntimeCredentials GetOrCreateRuntimeCredentials(Project project)
    {
        if (project.RuntimePasswordCiphertext is not null &&
            secrets.TryUnprotect(project.RuntimePasswordCiphertext, out var storedPassword))
        {
            return new ProjectRuntimeCredentials(ProjectNaming.ToRuntimeRole(project.Slug), storedPassword);
        }

        // No credentials yet, or the encryption key changed: generate a new password and
        // re-protect it. Existing Runtime sessions with the old password simply end.
        var password = RuntimePasswordGenerator.Create();
        project.RuntimePasswordCiphertext = secrets.Protect(password);
        return new ProjectRuntimeCredentials(ProjectNaming.ToRuntimeRole(project.Slug), password);
    }

    private static bool TryParseKind(string? kind, out ApiKeyKind parsedKind, out string? error)
    {
        parsedKind = ApiKeyKind.Public;

        if (string.IsNullOrWhiteSpace(kind))
        {
            error = null;
            return true;
        }

        if (string.Equals(kind, "public", StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        if (string.Equals(kind, "secret", StringComparison.OrdinalIgnoreCase))
        {
            parsedKind = ApiKeyKind.Secret;
            error = null;
            return true;
        }

        error = "Kind must be 'public' or 'secret'.";
        return false;
    }

    private static bool TryValidateExpiry(DateTimeOffset? expiresAt, out string? error)
    {
        if (expiresAt is null)
        {
            error = null;
            return true;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            error = "Expiration must be in the future.";
            return false;
        }

        error = null;
        return true;
    }

    private static AuditEntry Audit(string action, Guid targetId, Guid projectId, object details) => new()
    {
        Actor = AdminActor,
        Action = action,
        TargetType = action.StartsWith("api_key", StringComparison.Ordinal) ? "api_key" : "project",
        TargetId = targetId.ToString(),
        ProjectId = projectId,
        Details = JsonSerializer.Serialize(details)
    };

    private static ControlResult<Project> NotFound(Guid id) =>
        ControlResult<Project>.Fail(ControlErrorCode.NotFound, $"Project '{id}' was not found.");

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
