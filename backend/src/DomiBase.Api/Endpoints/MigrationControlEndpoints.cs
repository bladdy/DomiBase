using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints for versioned project migrations. Every route is protected by the
/// administrative middleware and lives under <c>/api/v1</c>; the Runtime Plane never exposes it.
/// The client sends typed operations, never SQL text.
/// </summary>
public static class MigrationControlEndpoints
{
    public static IEndpointRouteBuilder MapMigrationControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}/migrations").WithTags("Migration Control");

        group.MapGet("", async (Guid projectId, MigrationService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ListAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    migrations => Results.Ok(migrations.Select(AppliedMigrationResponse.From)));
            })
            .WithName("ControlListProjectMigrations");

        group.MapPost("", async (
            Guid projectId,
            HttpRequest request,
            MigrationService service,
            CancellationToken cancellationToken) =>
            {
                JsonElement body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<JsonElement>(
                        request.Body,
                        SchemaRequestParser.JsonOptions,
                        cancellationToken);
                }
                catch (JsonException)
                {
                    return Invalid("The request body must be valid JSON.");
                }

                if (body.ValueKind != JsonValueKind.Object
                    || !body.TryGetProperty("version", out var versionElement)
                    || !versionElement.TryGetInt32(out var version))
                {
                    return Invalid("A numeric 'version' is required.");
                }

                var nameError = RequiredText(body, "name");
                if (nameError is not null)
                {
                    return Invalid(nameError);
                }

                if (!TryParseOperations(body, out var operations, out var operationError))
                {
                    return Invalid(operationError);
                }

                var result = await service.ApplyAsync(
                    projectId,
                    version,
                    body.GetProperty("name").GetString(),
                    operations,
                    cancellationToken);

                return ControlResultMapper.ToHttp(result, summary => Results.Ok(MigrationSummaryResponse.From(summary)));
            })
            .WithName("ControlApplyProjectMigration");

        return app;
    }

    private static bool TryParseOperations(
        JsonElement body,
        out IReadOnlyList<SchemaMutation>? operations,
        out string? error)
    {
        operations = null;

        if (!body.TryGetProperty("operations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "An 'operations' array is required.";
            return false;
        }

        var parsed = new List<SchemaMutation>();
        try
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    error = "Every operation needs an object with a 'type'.";
                    return false;
                }

                var type = typeElement.GetString();
                if (!TryParseOperation(type!, element, out var mutation, out error))
                {
                    return false;
                }

                parsed.Add(mutation!);
            }
        }
        catch (JsonException)
        {
            error = "An operation has an invalid shape.";
            return false;
        }

        operations = parsed;
        error = null;
        return true;
    }

    private static bool TryParseOperation(
        string type,
        JsonElement element,
        out SchemaMutation? mutation,
        out string? error)
    {
        mutation = null;
        error = null;

        switch (type)
        {
            case "create_table":
            {
                var request = element.Deserialize<CreateTableRequest>(SchemaRequestParser.JsonOptions);
                if (request is null)
                {
                    error = "A create_table operation requires a valid table definition.";
                    return false;
                }

                if (!SchemaRequestParser.TryParseColumns(request.Columns, out var columns, out error))
                {
                    return false;
                }

                mutation = new CreateTableMutation(request.Name, columns!);
                return true;
            }

            case "drop_table":
                return TryRequireText(element, "table", out var dropTable, out error)
                       && Assign(out mutation, new DropTableMutation(dropTable!));

            case "add_column":
            {
                if (!TryRequireText(element, "table", out var table, out error)
                    || !SchemaRequestParser.TryParseColumn(
                        element.Deserialize<ColumnRequest>(SchemaRequestParser.JsonOptions), out var column, out error))
                {
                    return false;
                }

                mutation = new AddColumnMutation(table!, column!);
                return true;
            }

            case "alter_column":
            {
                if (!TryRequireText(element, "table", out var table, out error)
                    || !TryRequireText(element, "column", out var column, out error))
                {
                    return false;
                }

                var request = element.Deserialize<AlterColumnRequest>(SchemaRequestParser.JsonOptions);
                mutation = new AlterColumnMutation(table!, column!, request?.Name, request?.Type, request?.Nullable);
                return true;
            }

            case "drop_column":
                return TryRequireText(element, "table", out var dropColumnTable, out error)
                       && TryRequireText(element, "column", out var dropColumn, out error)
                       && Assign(out mutation, new DropColumnMutation(dropColumnTable!, dropColumn!));

            case "create_index":
            {
                if (!TryRequireText(element, "table", out var table, out error))
                {
                    return false;
                }

                var request = element.Deserialize<CreateIndexRequest>(SchemaRequestParser.JsonOptions);
                mutation = new CreateIndexMutation(table!, request?.Name ?? string.Empty, request?.Columns ?? [], request?.Unique ?? false);
                return true;
            }

            case "drop_index":
                return TryRequireText(element, "table", out var indexTable, out error)
                       && TryRequireText(element, "index", out var index, out error)
                       && Assign(out mutation, new DropIndexMutation(indexTable!, index!));

            case "create_policy":
            case "alter_policy":
            {
                if (!TryRequireText(element, "table", out var table, out error))
                {
                    return false;
                }

                var request = element.Deserialize<PolicyRequest>(SchemaRequestParser.JsonOptions);
                if (!SchemaRequestParser.TryParsePolicy(request, out var policy, out error))
                {
                    return false;
                }

                var name = type == "alter_policy" && TryRequireText(element, "policy", out var alterPolicyName, out error)
                    ? alterPolicyName!
                    : policy.Name;

                mutation = type == "create_policy"
                    ? new CreatePolicyMutation(table!, name, policy.Commands, policy.Roles, policy.Using, policy.WithCheck)
                    : new AlterPolicyMutation(table!, name, policy.Commands, policy.Roles, policy.Using, policy.WithCheck);
                return true;
            }

            case "drop_policy":
                return TryRequireText(element, "table", out var policyTable, out error)
                       && TryRequireText(element, "policy", out var policyName, out error)
                       && Assign(out mutation, new DropPolicyMutation(policyTable!, policyName!));

            default:
                error = $"Unsupported migration operation '{type}'.";
                return false;
        }
    }

    private static bool Assign<T>(out SchemaMutation? mutation, T value) where T : SchemaMutation
    {
        mutation = value;
        return true;
    }

    private static bool TryRequireText(JsonElement element, string property, out string? value, out string? error)
    {
        error = RequiredText(element, property);
        value = error is null ? element.GetProperty(property).GetString() : null;
        return error is null;
    }

    private static string? RequiredText(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value)
        || value.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(value.GetString())
            ? $"Operation requires a non-empty '{property}'."
            : null;

    private static IResult Invalid(string? hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<MigrationSummary>.Fail(
                ControlErrorCode.Validation,
                "The migration is not valid.",
                hint),
            _ => Results.Ok());
}

public sealed record AppliedMigrationResponse(
    int Version,
    string Name,
    string Checksum,
    int OperationCount,
    DateTimeOffset AppliedAt)
{
    public static AppliedMigrationResponse From(AppliedMigration migration) =>
        new(migration.Version, migration.Name, migration.Checksum, migration.OperationCount, migration.AppliedAt);
}

public sealed record MigrationSummaryResponse(
    int Version,
    string Name,
    string Checksum,
    int OperationCount,
    bool AlreadyApplied)
{
    public static MigrationSummaryResponse From(MigrationSummary summary) =>
        new(summary.Version, summary.Name, summary.Checksum, summary.OperationCount, summary.AlreadyApplied);
}
