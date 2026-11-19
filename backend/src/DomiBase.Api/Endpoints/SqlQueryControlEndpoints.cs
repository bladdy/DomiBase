using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Projects;
using DomiBase.Application.Query;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Administrative SQL editor. Protected by the Control Plane middleware, bounded by the
/// application service and never exposed under <c>/rest/v1</c>. The client sends a read-only
/// statement; the server validates and executes it against a single project database.
/// </summary>
public static class SqlQueryControlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSqlQueryControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/projects/{projectId:guid}/sql/query", async (
            Guid projectId,
            HttpRequest request,
            SqlQueryService service,
            CancellationToken cancellationToken) =>
            {
                SqlQueryRequest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<SqlQueryRequest>(
                        request.Body,
                        JsonOptions,
                        cancellationToken);
                }
                catch (JsonException)
                {
                    return Invalid("The request body must be valid JSON.");
                }

                if (body is null || string.IsNullOrWhiteSpace(body.Sql))
                {
                    return Invalid("A non-empty 'sql' statement is required.");
                }

                var result = await service.ExecuteAsync(
                    projectId,
                    body.Sql,
                    body.MaxRows,
                    body.TimeoutMs,
                    cancellationToken);

                return ControlResultMapper.ToHttp(result, query => Results.Ok(new SqlQueryResponse(
                    query.Columns,
                    query.Rows,
                    query.RowCount,
                    query.Truncated,
                    query.DurationMs)));
            })
            .WithName("ControlExecuteSqlQuery");

        return app;
    }

    private static IResult Invalid(string hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<SqlQueryResult>.Fail(
                ControlErrorCode.Validation,
                "The query request is not valid.",
                hint),
            _ => Results.Ok());

    private sealed record SqlQueryRequest(string? Sql, int? MaxRows, int? TimeoutMs);

    private sealed record SqlQueryResponse(
        IReadOnlyList<string> Columns,
        IReadOnlyList<JsonElement> Rows,
        int RowCount,
        bool Truncated,
        long DurationMs);
}
