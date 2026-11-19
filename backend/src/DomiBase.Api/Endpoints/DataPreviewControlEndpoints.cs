using System.Globalization;
using DomiBase.Api.Http;
using DomiBase.Application.Data;
using DomiBase.Application.Projects;
using DomiBase.Application.Runtime;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Administrative row preview for the panel. The request never carries a PostgreSQL role,
/// database name, API key or SQL: only a validated table, filters and bounds. Execution goes
/// through the Runtime data service, so RLS and grants are enforced as for public requests.
/// </summary>
public static class DataPreviewControlEndpoints
{
    public static IEndpointRouteBuilder MapDataPreviewControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/v1/projects/{projectId:guid}/data/tables/{table}/rows",
                async (
                    Guid projectId,
                    string table,
                    HttpContext http,
                    DataPreviewService service,
                    CancellationToken cancellationToken) =>
                {
                    if (!TryParseOptionalInt(http.Request, "limit", out var limit, out var error) ||
                        !TryParseOptionalInt(http.Request, "offset", out var offset, out error))
                    {
                        return Invalid(error);
                    }

                    var options = new RuntimeReadOptions(
                        ParseSelect(http.Request),
                        http.Request.Query["f"]
                            .Where(value => value is not null)
                            .Select(value => value!)
                            .ToList(),
                        TrimToNull(http.Request.Query["order"].ToString()),
                        limit,
                        offset);

                    var result = await service.PreviewAsync(projectId, table, options, cancellationToken);
                    return ControlResultMapper.ToHttp(result, preview => Results.Ok(new DataPreviewResponse(
                        preview.Role,
                        preview.Columns,
                        preview.Rows,
                        preview.RowCount,
                        preview.Truncated)));
                })
            .WithName("ControlPreviewTableRows");

        return app;
    }

    private static IResult Invalid(string? hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<DataPreviewResult>.Fail(
                ControlErrorCode.Validation,
                "The preview request is not valid.",
                hint),
            _ => Results.Ok());

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string>? ParseSelect(HttpRequest request)
    {
        var raw = request.Query["select"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var columns = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return columns.Count == 0 ? null : columns;
    }

    private static bool TryParseOptionalInt(
        HttpRequest request,
        string name,
        out int? value,
        out string? error)
    {
        value = null;
        var raw = request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        value = parsed;
        error = null;
        return true;
    }

    private sealed record DataPreviewResponse(
        string Role,
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        int RowCount,
        bool Truncated);
}
