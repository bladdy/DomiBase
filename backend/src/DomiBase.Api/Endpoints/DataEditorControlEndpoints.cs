using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Data;
using DomiBase.Application.Projects;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Administrative row editing for the Table Editor. The project is resolved from trusted
/// metadata and execution goes through the Runtime data service, so RLS and grants are
/// enforced exactly as for the public SDK. No SQL, role, database or API key is accepted.
/// </summary>
public static class DataEditorControlEndpoints
{
    public static IEndpointRouteBuilder MapDataEditorControlEndpoints(this IEndpointRouteBuilder app)
    {
        const string route = "/api/v1/projects/{projectId:guid}/data/tables/{table}/rows";

        app.MapPost(route, async (
                Guid projectId,
                string table,
                HttpContext http,
                DataEditorService service,
                CancellationToken cancellationToken) =>
            {
                var (body, bodyError) = await TryReadBodyAsync(http);
                if (bodyError is not null)
                {
                    return Invalid(bodyError);
                }

                var result = await service.InsertAsync(
                    projectId,
                    table,
                    body,
                    ParseSelect(http),
                    cancellationToken);
                return ControlResultMapper.ToHttp(result, edit => Results.Ok(new DataEditResponse(edit.Affected, edit.Rows)));
            })
            .WithName("ControlInsertTableRows");

        app.MapPatch(route, async (
                Guid projectId,
                string table,
                HttpContext http,
                DataEditorService service,
                CancellationToken cancellationToken) =>
            {
                var (body, bodyError) = await TryReadBodyAsync(http);
                if (bodyError is not null)
                {
                    return Invalid(bodyError);
                }

                var result = await service.UpdateAsync(
                    projectId,
                    table,
                    body,
                    ParseFilters(http),
                    ParseSelect(http),
                    cancellationToken);
                return ControlResultMapper.ToHttp(result, edit => Results.Ok(new DataEditResponse(edit.Affected, edit.Rows)));
            })
            .WithName("ControlUpdateTableRows");

        app.MapDelete(route, async (
                Guid projectId,
                string table,
                HttpContext http,
                DataEditorService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DeleteAsync(
                    projectId,
                    table,
                    ParseFilters(http),
                    ParseSelect(http),
                    cancellationToken);
                return ControlResultMapper.ToHttp(result, edit => Results.Ok(new DataEditResponse(edit.Affected, edit.Rows)));
            })
            .WithName("ControlDeleteTableRows");

        return app;
    }

    private static async Task<(JsonElement Body, string? Error)> TryReadBodyAsync(HttpContext http)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, "The request body must be valid JSON.");
        }
    }

    private static IReadOnlyList<string> ParseFilters(HttpContext http) =>
        http.Request.Query["f"]
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

    private static IReadOnlyList<string>? ParseSelect(HttpContext http)
    {
        var raw = http.Request.Query["select"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var columns = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return columns.Count == 0 ? null : columns;
    }

    private static IResult Invalid(string? hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<DataEditResult>.Fail(
                ControlErrorCode.Validation,
                "The row edit request is not valid.",
                hint),
            _ => Results.Ok());

    private sealed record DataEditResponse(
        int Affected,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
}
