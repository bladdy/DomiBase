using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Control Plane orchestration for versioned project migrations. Validates the whole plan
/// before privileged SQL runs, enforces <c>Ready</c> and project isolation, writes a non-secret
/// audit entry, and delegates persistence of the history to <see cref="IMigrationStore"/>.
/// </summary>
public sealed class MigrationService(
    IProjectRepository repository,
    IMigrationStore store,
    ProjectSchemaResolver resolver)
{
    public const int MinVersion = 1;
    public const int MaxVersion = 10_000;
    public const int MaxOperations = 100;

    public async Task<ControlResult<MigrationSummary>> ApplyAsync(
        Guid projectId,
        int version,
        string? name,
        IReadOnlyList<SchemaMutation>? operations,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildPlan(version, name, operations, out var plan, out var validationError))
        {
            return ControlResult<MigrationSummary>.Fail(
                ControlErrorCode.Validation,
                "The migration is not valid.",
                validationError);
        }

        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<MigrationSummary>.Fail(
                resolved.Error.Code,
                resolved.Error.Message,
                resolved.Error.Hint);
        }

        MigrationApplyOutcome outcome;
        try
        {
            outcome = await store.ApplyAsync(resolved.Names!, plan!, cancellationToken);
        }
        catch (SchemaAdminException exception)
        {
            return ControlResult<MigrationSummary>.Fail(
                exception.IsValidation ? ControlErrorCode.Validation : ControlErrorCode.Conflict,
                exception.Message,
                exception.Hint);
        }

        var summary = new MigrationSummary(
            outcome.Migration.Version,
            outcome.Migration.Name,
            outcome.Migration.Checksum,
            outcome.Migration.OperationCount,
            outcome.AlreadyApplied);

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = outcome.AlreadyApplied ? "migration.reapplied" : "migration.applied",
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolved.Names!.DatabaseName,
                    version = summary.Version,
                    name = summary.Name,
                    checksum = summary.Checksum,
                    operations = summary.OperationCount
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<MigrationSummary>.Ok(summary);
    }

    public async Task<ControlResult<IReadOnlyList<AppliedMigration>>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<IReadOnlyList<AppliedMigration>>.Fail(
                resolved.Error.Code,
                resolved.Error.Message,
                resolved.Error.Hint);
        }

        IReadOnlyList<AppliedMigration> migrations;
        try
        {
            migrations = await store.ListAsync(resolved.Names!, cancellationToken);
        }
        catch (SchemaAdminException exception)
        {
            return ControlResult<IReadOnlyList<AppliedMigration>>.Fail(
                exception.IsValidation ? ControlErrorCode.Validation : ControlErrorCode.Conflict,
                exception.Message,
                exception.Hint);
        }

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = "migration.list",
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolved.Names!.DatabaseName,
                    count = migrations.Count
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<IReadOnlyList<AppliedMigration>>.Ok(migrations);
    }

    /// <summary>Builds a plan only from server-validated values; never accepts raw SQL.</summary>
    public static bool TryBuildPlan(
        int version,
        string? name,
        IReadOnlyList<SchemaMutation>? operations,
        out MigrationPlan? plan,
        out string? error)
    {
        plan = null;

        if (version < MinVersion || version > MaxVersion)
        {
            error = $"Migration version must be between {MinVersion} and {MaxVersion}.";
            return false;
        }

        if (!SchemaDdlValidator.IsIdentifier(name))
        {
            error = "Migration name must be a lowercase SQL-safe identifier.";
            return false;
        }

        if (operations is null || operations.Count == 0 || operations.Count > MaxOperations)
        {
            error = $"A migration requires between 1 and {MaxOperations} operations.";
            return false;
        }

        foreach (var operation in operations)
        {
            if (!SchemaDdlValidator.TryValidate(operation, out error))
            {
                return false;
            }
        }

        plan = new MigrationPlan(version, name!, operations, MigrationChecksum.Compute(operations));
        error = null;
        return true;
    }
}
