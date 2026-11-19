using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Control Plane orchestration for administrative schema changes. It resolves the project
/// from trusted metadata, enforces <c>Ready</c>, validates the whole mutation, delegates
/// privileged SQL to <see cref="IProjectSchemaAdmin"/> and records a non-secret audit entry.
/// </summary>
public sealed class SchemaAdminService(
    IProjectRepository repository,
    IProjectSchemaAdmin schemaAdmin,
    ProjectSchemaResolver resolver)
{
    public async Task<ControlResult<SchemaMutationSummary>> ExecuteAsync(
        Guid projectId,
        SchemaMutation mutation,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.TryValidate(mutation, out var validationError))
        {
            return ControlResult<SchemaMutationSummary>.Fail(
                ControlErrorCode.Validation,
                "The schema operation is not valid.",
                validationError);
        }

        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<SchemaMutationSummary>.Fail(
                resolved.Error.Code,
                resolved.Error.Message,
                resolved.Error.Hint);
        }

        try
        {
            await schemaAdmin.ExecuteAsync(resolved.Names!, mutation, cancellationToken);
        }
        catch (SchemaAdminException exception)
        {
            return ControlResult<SchemaMutationSummary>.Fail(
                exception.IsValidation ? ControlErrorCode.Validation : ControlErrorCode.Conflict,
                exception.Message,
                exception.Hint);
        }

        var summary = Describe(mutation);

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = mutation.AuditAction,
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolved.Names!.DatabaseName,
                    schema = SchemaService.ExposedSchema,
                    table = summary.Table,
                    @object = summary.Object
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<SchemaMutationSummary>.Ok(summary);
    }

    public async Task<ControlResult<IReadOnlyList<SchemaPolicy>>> ListPoliciesAsync(
        Guid projectId,
        string table,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return ControlResult<IReadOnlyList<SchemaPolicy>>.Fail(
                ControlErrorCode.Validation,
                "The table name is not valid.");
        }

        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<IReadOnlyList<SchemaPolicy>>.Fail(
                resolved.Error.Code,
                resolved.Error.Message,
                resolved.Error.Hint);
        }

        IReadOnlyList<SchemaPolicy> policies;
        try
        {
            policies = await schemaAdmin.ListPoliciesAsync(resolved.Names!, table, cancellationToken);
        }
        catch (SchemaAdminException exception)
        {
            return ControlResult<IReadOnlyList<SchemaPolicy>>.Fail(
                exception.IsValidation ? ControlErrorCode.Validation : ControlErrorCode.Conflict,
                exception.Message,
                exception.Hint);
        }

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = "schema.policies",
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolved.Names!.DatabaseName,
                    schema = SchemaService.ExposedSchema,
                    table,
                    count = policies.Count
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<IReadOnlyList<SchemaPolicy>>.Ok(policies);
    }

    private static SchemaMutationSummary Describe(SchemaMutation mutation) => mutation switch
    {
        CreateTableMutation create => new(mutation.AuditAction, create.Table, null),
        DropTableMutation drop => new(mutation.AuditAction, drop.Table, null),
        AddColumnMutation add => new(mutation.AuditAction, add.Table, add.Column.Name),
        AlterColumnMutation alter => new(mutation.AuditAction, alter.Table, alter.NewName ?? alter.Column),
        DropColumnMutation dropColumn => new(mutation.AuditAction, dropColumn.Table, dropColumn.Column),
        CreateIndexMutation createIndex => new(mutation.AuditAction, createIndex.Table, createIndex.Name),
        DropIndexMutation dropIndex => new(mutation.AuditAction, dropIndex.Table, dropIndex.Name),
        CreatePolicyMutation createPolicy => new(mutation.AuditAction, createPolicy.Table, createPolicy.Name),
        AlterPolicyMutation alterPolicy => new(mutation.AuditAction, alterPolicy.Table, alterPolicy.Name),
        DropPolicyMutation dropPolicy => new(mutation.AuditAction, dropPolicy.Table, dropPolicy.Name),
        _ => new(mutation.AuditAction, "unknown", null)
    };
}
