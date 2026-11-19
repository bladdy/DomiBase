using System.Text.Json;
using DomiBase.Application.Schema;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Shared translation from HTTP DTOs into typed schema operations. Used by both direct
/// schema administration (P1.1) and versioned migrations (P1.3), so both surfaces apply
/// exactly the same parsing rules before the Application validator runs.
/// </summary>
internal static class SchemaRequestParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool TryParseColumns(
        IReadOnlyList<ColumnRequest>? requests,
        out IReadOnlyList<ColumnDefinition>? columns,
        out string? error)
    {
        columns = null;
        error = null;

        if (requests is null || requests.Count == 0)
        {
            error = "A table requires at least one column.";
            return false;
        }

        var parsed = new List<ColumnDefinition>(requests.Count);
        foreach (var request in requests)
        {
            if (!TryParseColumn(request, out var column, out error))
            {
                return false;
            }

            parsed.Add(column!);
        }

        columns = parsed;
        return true;
    }

    internal static bool TryParseColumn(ColumnRequest? request, out ColumnDefinition? column, out string? error)
    {
        column = null;

        if (request is null)
        {
            error = "A column definition is required.";
            return false;
        }

        if (!SchemaDdlValidator.TryBuildDefault(request.Default, out var defaultSql, out error))
        {
            return false;
        }

        ColumnReference? reference = null;
        if (request.References is not null)
        {
            reference = new ColumnReference(request.References.Table, request.References.Column);
        }

        column = new ColumnDefinition(
            request.Name,
            request.Type,
            request.Nullable,
            request.PrimaryKey,
            request.Unique,
            defaultSql,
            reference,
            request.Identity);
        return true;
    }

    internal static bool TryParsePolicy(
        PolicyRequest? request,
        out ParsedPolicy parsed,
        out string? error)
    {
        parsed = null!;

        if (request is null)
        {
            error = "A policy definition is required.";
            return false;
        }

        var commands = new List<PolicyCommand>();
        foreach (var raw in request.Commands ?? [])
        {
            if (!Enum.TryParse<PolicyCommand>(raw, true, out var command))
            {
                error = "Policy commands must be SELECT, INSERT, UPDATE or DELETE.";
                return false;
            }

            commands.Add(command);
        }

        var roles = new List<SchemaFunctionalRole>();
        foreach (var raw in request.Roles ?? [])
        {
            if (!Enum.TryParse<SchemaFunctionalRole>(raw, true, out var role))
            {
                error = "Policy roles must be anon or authenticated.";
                return false;
            }

            roles.Add(role);
        }

        var usingExpression = request.Using ?? request.WithCheck;
        var withCheckExpression = request.WithCheck ?? request.Using;
        if (string.IsNullOrWhiteSpace(usingExpression) || string.IsNullOrWhiteSpace(withCheckExpression))
        {
            error = "A policy requires a USING or WITH CHECK expression.";
            return false;
        }

        parsed = new ParsedPolicy(request.Name, commands, roles, usingExpression, withCheckExpression);
        error = null;
        return true;
    }

    internal sealed record ParsedPolicy(
        string Name,
        IReadOnlyList<PolicyCommand> Commands,
        IReadOnlyList<SchemaFunctionalRole> Roles,
        string Using,
        string WithCheck);
}

public sealed record ReferenceRequest(string Table, string Column);

public sealed record ColumnRequest(
    string Name,
    string Type,
    bool Nullable = true,
    bool PrimaryKey = false,
    bool Unique = false,
    JsonElement? Default = null,
    ReferenceRequest? References = null,
    bool Identity = false);

public sealed record PolicyRequest(
    string Name,
    IReadOnlyList<string>? Commands,
    IReadOnlyList<string>? Roles,
    string? Using = null,
    string? WithCheck = null);
