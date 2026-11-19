using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Runtime;
using DomiBase.Application.Schema;
using DomiBase.Domain.Auditing;

namespace DomiBase.Application.Data;

/// <summary>
/// Administrative row preview. It deliberately reuses the Runtime execution path so the
/// anonymous functional role, RLS policies and column grants are exactly the same as for a
/// real public request. The owner-level SQL editor remains a separate capability.
/// </summary>
public sealed record DataPreviewResult(
    string Role,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    bool Truncated);

public sealed class DataPreviewService(
    IRuntimeProjectResolver resolver,
    IRuntimeDataService data,
    IProjectRepository repository)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxOffset = 100_000;
    public const int MaxFilters = 10;
    public const int MaxSelectColumns = 50;

    public async Task<ControlResult<DataPreviewResult>> PreviewAsync(
        Guid projectId,
        string table,
        RuntimeReadOptions input,
        CancellationToken cancellationToken = default)
    {
        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            return ControlResult<DataPreviewResult>.Fail(
                ControlErrorCode.Validation,
                "The table name is not valid.",
                "Use a lowercase identifier from the project catalog.");
        }

        var resolution = await resolver.ResolveForProjectAsync(projectId, cancellationToken);
        if (resolution.Status != RuntimeResolveStatus.Success || resolution.Context is null)
        {
            return MapResolution(resolution.Status);
        }

        var select = input.Select;
        if (select is not null)
        {
            if (select.Count > MaxSelectColumns || select.Any(column => !SchemaDdlValidator.IsIdentifier(column)))
            {
                return ControlResult<DataPreviewResult>.Fail(
                    ControlErrorCode.Validation,
                    "The column selection is not valid.",
                    $"Use at most {MaxSelectColumns} lowercase column identifiers.");
            }
        }

        if (input.Filters.Count > MaxFilters ||
            input.Filters.Any(filter => string.IsNullOrWhiteSpace(filter) || filter.Length > 512))
        {
            return ControlResult<DataPreviewResult>.Fail(
                ControlErrorCode.Validation,
                "The filters are not valid.",
                $"Use at most {MaxFilters} filters in the form column.operator.value.");
        }

        if (input.Order is { Length: > 300 })
        {
            return ControlResult<DataPreviewResult>.Fail(
                ControlErrorCode.Validation,
                "The ordering is not valid.",
                "Use at most three order expressions.");
        }

        var limit = Math.Clamp(input.Limit ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Clamp(input.Offset ?? 0, 0, MaxOffset);
        var options = new RuntimeReadOptions(
            select,
            input.Filters,
            string.IsNullOrWhiteSpace(input.Order) ? null : input.Order,
            limit,
            offset);

        RuntimeResultSet result;
        try
        {
            // user = null is intentional: the preview always runs as the anonymous role.
            result = await data.SelectAsync(resolution.Context, table, options, null, cancellationToken);
        }
        catch (RuntimeQueryException exception)
        {
            return MapRuntimeFailure(exception);
        }

        var columns = result.Rows.Count == 0
            ? []
            : result.Rows[0].Keys.ToArray();
        var truncated = result.Rows.Count >= limit;

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = "data.preview",
                TargetType = "table",
                TargetId = table,
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolution.Context.DatabaseName,
                    role = "anon",
                    rowCount = result.Rows.Count,
                    truncated,
                    limit,
                    offset,
                    filterCount = options.Filters.Count
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<DataPreviewResult>.Ok(new DataPreviewResult(
            "anon",
            columns,
            result.Rows,
            result.Rows.Count,
            truncated));
    }

    private static ControlResult<DataPreviewResult> MapResolution(RuntimeResolveStatus status) => status switch
    {
        RuntimeResolveStatus.ProjectNotFound => ControlResult<DataPreviewResult>.Fail(
            ControlErrorCode.NotFound,
            "The project does not exist."),
        RuntimeResolveStatus.ProjectNotReady => ControlResult<DataPreviewResult>.Fail(
            ControlErrorCode.Conflict,
            "The project is not ready.",
            "Row previews are available only for Ready projects."),
        _ => ControlResult<DataPreviewResult>.Fail(
            ControlErrorCode.Conflict,
            "The project cannot be reached right now.")
    };

    private static ControlResult<DataPreviewResult> MapRuntimeFailure(RuntimeQueryException exception)
    {
        var code = exception.StatusCode switch
        {
            400 => ControlErrorCode.Validation,
            404 => ControlErrorCode.NotFound,
            _ => ControlErrorCode.Conflict
        };

        return ControlResult<DataPreviewResult>.Fail(code, exception.Message, exception.Hint);
    }
}
