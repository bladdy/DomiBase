using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Runtime;
using DomiBase.Application.Schema;
using DomiBase.Domain.Auditing;

namespace DomiBase.Application.Data;

/// <summary>
/// Administrative row editing for the panel. Like the preview, it reuses the Runtime
/// execution path so the anonymous role, RLS policies and column grants are enforced
/// exactly as they are for public SDK requests. No database name, role or SQL is accepted
/// from the caller.
/// </summary>
public sealed record DataEditResult(
    int Affected,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public sealed class DataEditorService(
    IRuntimeProjectResolver resolver,
    IRuntimeDataService data,
    IProjectRepository repository)
{
    public const int MaxFilters = 10;
    public const int MaxInsertRows = 100;

    public async Task<ControlResult<DataEditResult>> InsertAsync(
        Guid projectId,
        string table,
        JsonElement body,
        IReadOnlyList<string>? select,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return Invalid("The table name is not valid.");
        }

        var resolution = await ResolveAsync(projectId, cancellationToken);
        if (resolution.Result is not null)
        {
            return resolution.Result;
        }

        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows;
        try
        {
            rows = RuntimeJsonValues.ReadMany(body);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        if (rows.Count > MaxInsertRows)
        {
            return Invalid($"At most {MaxInsertRows} rows can be inserted at once.");
        }

        if (!ValidSelect(select))
        {
            return Invalid("The column selection is not valid.");
        }

        RuntimeResultSet result;
        try
        {
            result = await data.InsertAsync(resolution.Context!, table, rows, select, null, cancellationToken);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        return await CommitAsync(
            projectId,
            table,
            "data.insert",
            resolution.Context!,
            result,
            new { rowCount = rows.Count },
            cancellationToken);
    }

    public async Task<ControlResult<DataEditResult>> UpdateAsync(
        Guid projectId,
        string table,
        JsonElement body,
        IReadOnlyList<string> filters,
        IReadOnlyList<string>? select,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return Invalid("The table name is not valid.");
        }

        var resolution = await ResolveAsync(projectId, cancellationToken);
        if (resolution.Result is not null)
        {
            return resolution.Result;
        }

        if (!ValidFilters(filters))
        {
            return Invalid($"Updates require between 1 and {MaxFilters} bounded filters.");
        }

        if (!ValidSelect(select))
        {
            return Invalid("The column selection is not valid.");
        }

        IReadOnlyDictionary<string, string?> values;
        try
        {
            values = RuntimeJsonValues.ReadObject(body);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        RuntimeResultSet result;
        try
        {
            result = await data.UpdateAsync(
                resolution.Context!,
                table,
                values,
                new RuntimeWriteOptions(select, filters),
                null,
                cancellationToken);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        return await CommitAsync(
            projectId,
            table,
            "data.update",
            resolution.Context!,
            result,
            new { filterCount = filters.Count, fieldCount = values.Count },
            cancellationToken);
    }

    public async Task<ControlResult<DataEditResult>> DeleteAsync(
        Guid projectId,
        string table,
        IReadOnlyList<string> filters,
        IReadOnlyList<string>? select,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return Invalid("The table name is not valid.");
        }

        var resolution = await ResolveAsync(projectId, cancellationToken);
        if (resolution.Result is not null)
        {
            return resolution.Result;
        }

        if (!ValidFilters(filters))
        {
            return Invalid($"Deletes require between 1 and {MaxFilters} bounded filters.");
        }

        if (!ValidSelect(select))
        {
            return Invalid("The column selection is not valid.");
        }

        RuntimeResultSet result;
        try
        {
            result = await data.DeleteAsync(
                resolution.Context!,
                table,
                new RuntimeWriteOptions(select, filters),
                null,
                cancellationToken);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        return await CommitAsync(
            projectId,
            table,
            "data.delete",
            resolution.Context!,
            result,
            new { filterCount = filters.Count },
            cancellationToken);
    }

    private async Task<(ControlResult<DataEditResult>? Result, RuntimeProjectContext? Context)> ResolveAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveForProjectAsync(projectId, cancellationToken);
        if (resolution.Status == RuntimeResolveStatus.Success && resolution.Context is not null)
        {
            return (null, resolution.Context);
        }

        return (resolution.Status switch
        {
            RuntimeResolveStatus.ProjectNotFound => ControlResult<DataEditResult>.Fail(
                ControlErrorCode.NotFound,
                "The project does not exist."),
            RuntimeResolveStatus.ProjectNotReady => ControlResult<DataEditResult>.Fail(
                ControlErrorCode.Conflict,
                "The project is not ready.",
                "Row editing is available only for Ready projects."),
            _ => ControlResult<DataEditResult>.Fail(
                ControlErrorCode.Conflict,
                "The project cannot be reached right now.")
        }, null);
    }

    private async Task<ControlResult<DataEditResult>> CommitAsync(
        Guid projectId,
        string table,
        string action,
        RuntimeProjectContext context,
        RuntimeResultSet result,
        object details,
        CancellationToken cancellationToken)
    {
        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = action,
                TargetType = "table",
                TargetId = table,
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = context.DatabaseName,
                    role = "anon",
                    affected = result.Affected,
                    extra = details
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<DataEditResult>.Ok(new DataEditResult(result.Affected, result.Rows));
    }

    private static bool ValidFilters(IReadOnlyList<string> filters) =>
        filters.Count is > 0 and <= MaxFilters &&
        filters.All(filter => !string.IsNullOrWhiteSpace(filter) && filter.Length <= 512);

    private static bool ValidSelect(IReadOnlyList<string>? select) =>
        select is null ||
        (select.Count is <= DataPreviewService.MaxSelectColumns &&
         select.All(SchemaDdlValidator.IsIdentifier));

    private static ControlResult<DataEditResult> Invalid(string hint) =>
        ControlResult<DataEditResult>.Fail(
            ControlErrorCode.Validation,
            "The row edit request is not valid.",
            hint);

    private static ControlResult<DataEditResult> MapRuntimeFailure(RuntimeQueryException exception)
    {
        var code = exception.StatusCode switch
        {
            400 => ControlErrorCode.Validation,
            404 => ControlErrorCode.NotFound,
            _ => ControlErrorCode.Conflict
        };

        return ControlResult<DataEditResult>.Fail(code, exception.Message, exception.Hint);
    }
}
