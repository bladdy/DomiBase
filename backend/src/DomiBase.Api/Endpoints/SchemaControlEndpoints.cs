using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints for project schema inspection and administration. Every route is
/// protected by the administrative middleware. No SQL, schema name, PostgreSQL role name or
/// raw policy expression is accepted: all input is parsed into validated typed operations.
/// The Runtime Plane never exposes these routes.
/// </summary>
public static class SchemaControlEndpoints
{
    public static IEndpointRouteBuilder MapSchemaControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}/schema").WithTags("Schema Control");

        group.MapGet("/tables", async (Guid projectId, SchemaService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ListTablesAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    tables => Results.Ok(tables.Select(SchemaTableResponse.From)));
            })
            .WithName("ControlListSchemaTables");

        group.MapGet("/views", async (Guid projectId, SchemaService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ListViewsAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    views => Results.Ok(views.Select(SchemaViewResponse.From)));
            })
            .WithName("ControlListSchemaViews");

        group.MapGet("/functions", async (Guid projectId, SchemaService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ListFunctionsAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    functions => Results.Ok(functions.Select(SchemaFunctionResponse.From)));
            })
            .WithName("ControlListSchemaFunctions");

        group.MapGet("/tables/{table}/columns", async (
            Guid projectId,
            string table,
            SchemaService service,
            CancellationToken cancellationToken) =>
            {
                var result = await service.ListColumnsAsync(projectId, table, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    columns => Results.Ok(columns.Select(SchemaColumnResponse.From)));
            })
            .WithName("ControlListSchemaColumns");

        group.MapPost("/tables", async (
            Guid projectId,
            CreateTableRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            {
                if (!SchemaRequestParser.TryParseColumns(request.Columns, out var columns, out var error))
                {
                    return Invalid(error);
                }

                return await ExecuteAsync(
                    service,
                    projectId,
                    new CreateTableMutation(request.Name, columns!),
                    cancellationToken);
            })
            .WithName("ControlCreateSchemaTable");

        group.MapDelete("/tables/{table}", async (
            Guid projectId,
            string table,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(service, projectId, new DropTableMutation(table), cancellationToken))
            .WithName("ControlDropSchemaTable");

        group.MapPost("/tables/{table}/columns", async (
            Guid projectId,
            string table,
            ColumnRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            {
                if (!SchemaRequestParser.TryParseColumn(request, out var column, out var error))
                {
                    return Invalid(error);
                }

                return await ExecuteAsync(
                    service,
                    projectId,
                    new AddColumnMutation(table, column!),
                    cancellationToken);
            })
            .WithName("ControlAddSchemaColumn");

        group.MapPatch("/tables/{table}/columns/{column}", async (
            Guid projectId,
            string table,
            string column,
            AlterColumnRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                service,
                projectId,
                new AlterColumnMutation(table, column, request.Name, request.Type, request.Nullable),
                cancellationToken))
            .WithName("ControlAlterSchemaColumn");

        group.MapDelete("/tables/{table}/columns/{column}", async (
            Guid projectId,
            string table,
            string column,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(service, projectId, new DropColumnMutation(table, column), cancellationToken))
            .WithName("ControlDropSchemaColumn");

        group.MapPost("/tables/{table}/indexes", async (
            Guid projectId,
            string table,
            CreateIndexRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                service,
                projectId,
                new CreateIndexMutation(table, request.Name, request.Columns ?? [], request.Unique),
                cancellationToken))
            .WithName("ControlCreateSchemaIndex");

        group.MapDelete("/tables/{table}/indexes/{index}", async (
            Guid projectId,
            string table,
            string index,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(service, projectId, new DropIndexMutation(table, index), cancellationToken))
            .WithName("ControlDropSchemaIndex");

        group.MapGet("/tables/{table}/policies", async (
            Guid projectId,
            string table,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            {
                var result = await service.ListPoliciesAsync(projectId, table, cancellationToken);
                return ControlResultMapper.ToHttp(result, policies => Results.Ok(policies.Select(PolicyResponse.From)));
            })
            .WithName("ControlListSchemaPolicies");

        group.MapPost("/tables/{table}/policies", async (
            Guid projectId,
            string table,
            PolicyRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            {
                if (!SchemaRequestParser.TryParsePolicy(request, out var parsed, out var error))
                {
                    return Invalid(error);
                }

                return await ExecuteAsync(
                    service,
                    projectId,
                    new CreatePolicyMutation(table, parsed.Name, parsed.Commands, parsed.Roles, parsed.Using, parsed.WithCheck),
                    cancellationToken);
            })
            .WithName("ControlCreateSchemaPolicy");

        group.MapPatch("/tables/{table}/policies/{policy}", async (
            Guid projectId,
            string table,
            string policy,
            PolicyRequest request,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            {
                if (!SchemaRequestParser.TryParsePolicy(request, out var parsed, out var error))
                {
                    return Invalid(error);
                }

                return await ExecuteAsync(
                    service,
                    projectId,
                    new AlterPolicyMutation(table, policy, parsed.Commands, parsed.Roles, parsed.Using, parsed.WithCheck),
                    cancellationToken);
            })
            .WithName("ControlAlterSchemaPolicy");

        group.MapDelete("/tables/{table}/policies/{policy}", async (
            Guid projectId,
            string table,
            string policy,
            SchemaAdminService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(service, projectId, new DropPolicyMutation(table, policy), cancellationToken))
            .WithName("ControlDropSchemaPolicy");

        return app;
    }

    private static async Task<IResult> ExecuteAsync(
        SchemaAdminService service,
        Guid projectId,
        SchemaMutation mutation,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteAsync(projectId, mutation, cancellationToken);
        return ControlResultMapper.ToHttp(result, summary => Results.Ok(MutationResponse.From(summary)));
    }

    private static IResult Invalid(string? hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<SchemaMutationSummary>.Fail(
                ControlErrorCode.Validation,
                "The schema operation is not valid.",
                hint),
            _ => Results.Ok());

}

public sealed record SchemaTableResponse(string Name)
{
    public static SchemaTableResponse From(SchemaTable table) => new(table.Name);
}

public sealed record SchemaViewResponse(string Name)
{
    public static SchemaViewResponse From(SchemaView view) => new(view.Name);
}

public sealed record SchemaColumnResponse(
    string Name,
    string Type,
    string DataType,
    bool Nullable,
    string? Default,
    bool PrimaryKey,
    bool Unique,
    bool Identity,
    string? ForeignTable,
    string? ForeignColumn)
{
    public static SchemaColumnResponse From(SchemaColumn column) => new(
        column.Name,
        column.Type,
        column.DataType,
        column.Nullable,
        column.Default,
        column.PrimaryKey,
        column.Unique,
        column.Identity,
        column.ForeignTable,
        column.ForeignColumn);
}

public sealed record SchemaFunctionResponse(string Name, string Arguments, string ReturnType)
{
    public static SchemaFunctionResponse From(SchemaFunction function) =>
        new(function.Name, function.Arguments, function.ReturnType);
}

public sealed record MutationResponse(string Action, string Table, string? Object)
{
    public static MutationResponse From(SchemaMutationSummary summary) =>
        new(summary.Action, summary.Table, summary.Object);
}

public sealed record PolicyResponse(
    string Name,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Roles,
    string? Using,
    string? WithCheck)
{
    public static PolicyResponse From(SchemaPolicy policy) =>
        new(policy.Name, policy.Commands, policy.Roles, policy.Using, policy.WithCheck);
}

public sealed record CreateTableRequest(string Name, IReadOnlyList<ColumnRequest> Columns);

public sealed record AlterColumnRequest(string? Name, string? Type, bool? Nullable);

public sealed record CreateIndexRequest(string Name, IReadOnlyList<string>? Columns, bool Unique = false);
