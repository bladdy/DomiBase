using System.Text.Json;
using System.Text.RegularExpressions;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Projects;
using DomiBase.Application.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Control Plane orchestration for read-only schema inspection. It resolves the project
/// from trusted metadata, fixes the inspected schema to <c>public</c>, and audits every read.
/// No identifier or SQL fragment is ever accepted from the caller.
/// </summary>
public sealed class SchemaService(
    IProjectRepository repository,
    IProjectCatalogReader catalog)
{
    /// <summary>The only schema exposed by the inspector. Internal schemas stay hidden.</summary>
    public const string ExposedSchema = "public";

    private static readonly Regex DatabaseNamePattern =
        new(@"^domibase_[a-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<ControlResult<IReadOnlyList<SchemaTable>>> ListTablesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        InspectAsync(projectId, "schema.tables", catalog.ListTablesAsync, cancellationToken);

    public Task<ControlResult<IReadOnlyList<SchemaView>>> ListViewsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        InspectAsync(projectId, "schema.views", catalog.ListViewsAsync, cancellationToken);

    public Task<ControlResult<IReadOnlyList<SchemaFunction>>> ListFunctionsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        InspectAsync(projectId, "schema.functions", catalog.ListFunctionsAsync, cancellationToken);

    public Task<ControlResult<IReadOnlyList<SchemaColumn>>> ListColumnsAsync(
        Guid projectId,
        string table,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return Task.FromResult(ControlResult<IReadOnlyList<SchemaColumn>>.Fail(
                ControlErrorCode.Validation,
                "The table name is not valid."));
        }

        return InspectTableAsync(
            projectId,
            table,
            "schema.columns",
            catalog.ListColumnsAsync,
            cancellationToken);
    }

    private async Task<ControlResult<IReadOnlyList<T>>> InspectTableAsync<T>(
        Guid projectId,
        string table,
        string auditAction,
        Func<string, string, CancellationToken, Task<IReadOnlyList<T>>> read,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found.");
        }

        if (project.Status != ProjectStatus.Ready)
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.Conflict,
                "The project database is not ready. Schema inspection requires status Ready.");
        }

        var database = project.DatabaseName;
        if (!DatabaseNamePattern.IsMatch(database))
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.Validation,
                "The project database name is not a valid DomiBase database name.");
        }

        var objects = await read(database, table, cancellationToken);

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = auditAction,
                TargetType = "table",
                TargetId = table,
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database,
                    schema = ExposedSchema,
                    table,
                    count = objects.Count
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<IReadOnlyList<T>>.Ok(objects);
    }

    private async Task<ControlResult<IReadOnlyList<T>>> InspectAsync<T>(
        Guid projectId,
        string auditAction,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> read,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found.");
        }

        if (project.Status != ProjectStatus.Ready)
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.Conflict,
                "The project database is not ready. Schema inspection requires status Ready.",
                "Retry once provisioning has completed.");
        }

        var database = project.DatabaseName;
        if (!DatabaseNamePattern.IsMatch(database))
        {
            return ControlResult<IReadOnlyList<T>>.Fail(
                ControlErrorCode.Validation,
                "The project database name is not a valid DomiBase database name.");
        }

        var objects = await read(database, cancellationToken);

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = auditAction,
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new { database, schema = ExposedSchema, count = objects.Count })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<IReadOnlyList<T>>.Ok(objects);
    }
}
