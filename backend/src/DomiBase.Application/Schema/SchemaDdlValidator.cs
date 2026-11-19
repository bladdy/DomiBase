using System.Text.Json;
using System.Text.RegularExpressions;

namespace DomiBase.Application.Schema;

/// <summary>
/// Single gate for every administrative schema change. It accepts only identifiers,
/// allowlisted types, bounded literals and fixed policy expressions. Anything else is
/// rejected before a privileged connection is opened, so no caller-controlled text can
/// reach PostgreSQL DDL.
/// </summary>
public static partial class SchemaDdlValidator
{
    public const int MaxIdentifierLength = 63;
    public const int MaxDefaultLength = 256;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "uuid", "text", "varchar", "char",
        "int2", "smallint", "int4", "integer", "int", "int8", "bigint",
        "boolean", "bool",
        "timestamptz", "timestamp", "date", "time", "timetz", "interval",
        "jsonb", "json", "numeric", "decimal", "float4", "float8",
        "bytea", "inet", "cidr", "macaddr", "money", "xml"
    };

    private static readonly HashSet<string> IdentityTypes = new(StringComparer.Ordinal)
    {
        "int2", "smallint", "int4", "integer", "int", "int8", "bigint"
    };

    private static readonly HashSet<string> AllowedPolicyExpressions = new(StringComparer.Ordinal)
    {
        "true",
        "false",
        "project_id = current_setting('domibase.project_id', true)::uuid"
    };

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^varchar\(([1-9][0-9]{0,3})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex VarcharPattern();

    [GeneratedRegex(@"^-?[0-9]{1,18}(\.[0-9]{1,9})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteralPattern();

    public static bool IsIdentifier(string? value) =>
        value is not null
        && value.Length is > 0 and <= MaxIdentifierLength
        && !value.StartsWith("pg_", StringComparison.Ordinal)
        && IdentifierPattern().IsMatch(value);

    public static bool IsType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (AllowedTypes.Contains(value))
        {
            return true;
        }

        var match = VarcharPattern().Match(value);
        return match.Success && int.Parse(match.Groups[1].Value) <= 8000;
    }

    public static bool IsPolicyExpression(string? value) =>
        value is not null && AllowedPolicyExpressions.Contains(value);

    /// <summary>
    /// Converts a JSON default value into a safe SQL literal. Strings are quoted by
    /// doubling single quotes; control characters and backslashes are rejected so the
    /// result cannot change meaning under any standard_conforming_strings setting.
    /// </summary>
    public static bool TryBuildDefault(JsonElement? value, out string? sql, out string? error)
    {
        sql = null;
        error = null;

        if (value is null || value.Value.ValueKind == JsonValueKind.Null || value.Value.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        switch (value.Value.ValueKind)
        {
            case JsonValueKind.True:
                sql = "true";
                return true;

            case JsonValueKind.False:
                sql = "false";
                return true;

            case JsonValueKind.Number:
                var raw = value.Value.GetRawText();
                if (!NumericLiteralPattern().IsMatch(raw))
                {
                    error = "Numeric defaults must be plain decimal numbers.";
                    return false;
                }

                sql = raw;
                return true;

            case JsonValueKind.String:
                var text = value.Value.GetString() ?? string.Empty;
                if (text.Length > MaxDefaultLength)
                {
                    error = $"Default values cannot exceed {MaxDefaultLength} characters.";
                    return false;
                }

                if (text is "gen_random_uuid()" or "now()")
                {
                    sql = text;
                    return true;
                }

                if (text.Any(character => character < ' ' || character is '\\' or ';' or '\u007f'))
                {
                    error = "Default values cannot contain control characters, backslashes or statement separators.";
                    return false;
                }

                sql = "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
                return true;

            default:
                error = "Default values must be a string, number, boolean or null.";
                return false;
        }
    }

    /// <summary>Validates a complete mutation. Returns <c>false</c> with a client-safe message.</summary>
    public static bool TryValidate(SchemaMutation mutation, out string? error)
    {
        error = null;

        switch (mutation)
        {
            case CreateTableMutation create:
                if (!IsIdentifier(create.Table))
                {
                    error = InvalidIdentifier("Table name");
                    return false;
                }

                if (create.Columns is null || create.Columns.Count == 0)
                {
                    error = "A table requires at least one column.";
                    return false;
                }

                if (create.Columns.Count > 100)
                {
                    error = "A table cannot have more than 100 columns.";
                    return false;
                }

                foreach (var column in create.Columns)
                {
                    if (!TryValidateColumn(column, out error))
                    {
                        return false;
                    }
                }

                if (create.Columns.Count(column => column.PrimaryKey) > 1)
                {
                    error = "Only one primary key column is supported.";
                    return false;
                }

                return true;

            case DropTableMutation drop:
                return RequireIdentifier(drop.Table, "Table name", out error);

            case AddColumnMutation add:
                return RequireIdentifier(add.Table, "Table name", out error)
                       && TryValidateColumn(add.Column, out error);

            case AlterColumnMutation alter:
                if (!RequireIdentifier(alter.Table, "Table name", out error)
                    || !RequireIdentifier(alter.Column, "Column name", out error))
                {
                    return false;
                }

                if (alter.NewName is not null && !IsIdentifier(alter.NewName))
                {
                    error = InvalidIdentifier("New column name");
                    return false;
                }

                if (alter.Type is not null && !IsType(alter.Type))
                {
                    error = "Column type is not allowed.";
                    return false;
                }

                if (alter.NewName is null && alter.Type is null && alter.Nullable is null)
                {
                    error = "At least one column change is required.";
                    return false;
                }

                return true;

            case DropColumnMutation dropColumn:
                return RequireIdentifier(dropColumn.Table, "Table name", out error)
                       && RequireIdentifier(dropColumn.Column, "Column name", out error);

            case CreateIndexMutation createIndex:
                if (!RequireIdentifier(createIndex.Table, "Table name", out error)
                    || !RequireIdentifier(createIndex.Name, "Index name", out error))
                {
                    return false;
                }

                if (createIndex.Columns is null || createIndex.Columns.Count == 0 || createIndex.Columns.Count > 16)
                {
                    error = "An index requires between 1 and 16 columns.";
                    return false;
                }

                foreach (var column in createIndex.Columns)
                {
                    if (!IsIdentifier(column))
                    {
                        error = InvalidIdentifier("Index column name");
                        return false;
                    }
                }

                return true;

            case DropIndexMutation dropIndex:
                return RequireIdentifier(dropIndex.Table, "Table name", out error)
                       && RequireIdentifier(dropIndex.Name, "Index name", out error);

            case CreatePolicyMutation createPolicy:
                return TryValidatePolicy(
                    createPolicy.Table,
                    createPolicy.Name,
                    createPolicy.Commands,
                    createPolicy.Roles,
                    createPolicy.Using,
                    createPolicy.WithCheck,
                    out error);

            case AlterPolicyMutation alterPolicy:
                return TryValidatePolicy(
                    alterPolicy.Table,
                    alterPolicy.Name,
                    alterPolicy.Commands,
                    alterPolicy.Roles,
                    alterPolicy.Using,
                    alterPolicy.WithCheck,
                    out error);

            case DropPolicyMutation dropPolicy:
                return RequireIdentifier(dropPolicy.Table, "Table name", out error)
                       && RequireIdentifier(dropPolicy.Name, "Policy name", out error);

            default:
                error = "Unsupported schema operation.";
                return false;
        }
    }

    private static bool TryValidateColumn(ColumnDefinition column, out string? error)
    {
        if (!IsIdentifier(column.Name))
        {
            error = InvalidIdentifier("Column name");
            return false;
        }

        if (!IsType(column.Type))
        {
            error = "Column type is not allowed.";
            return false;
        }

        if (column.Identity)
        {
            if (!IdentityTypes.Contains(column.Type))
            {
                error = "IDENTITY requires an integer column type.";
                return false;
            }

            if (column.DefaultSql is not null)
            {
                error = "An IDENTITY column cannot also define a DEFAULT value.";
                return false;
            }
        }

        if (column.Reference is not null
            && (!IsIdentifier(column.Reference.Table) || !IsIdentifier(column.Reference.Column)))
        {
            error = InvalidIdentifier("Foreign key reference");
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidatePolicy(
        string table,
        string name,
        IReadOnlyList<PolicyCommand>? commands,
        IReadOnlyList<SchemaFunctionalRole>? roles,
        string usingExpression,
        string withCheckExpression,
        out string? error)
    {
        if (!IsIdentifier(table) || !IsIdentifier(name))
        {
            error = InvalidIdentifier("Table or policy name");
            return false;
        }

        if (commands is null || commands.Count == 0 || commands.Count > 4 || commands.Distinct().Count() != commands.Count)
        {
            error = "Select between one and four distinct policy commands.";
            return false;
        }

        if (roles is null || roles.Count == 0 || roles.Count > 2 || roles.Distinct().Count() != roles.Count)
        {
            error = "Select at least one functional role.";
            return false;
        }

        if (!IsPolicyExpression(usingExpression) || !IsPolicyExpression(withCheckExpression))
        {
            error = "Policy expressions must be one of the supported safe templates.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RequireIdentifier(string? value, string label, out string? error)
    {
        if (IsIdentifier(value))
        {
            error = null;
            return true;
        }

        error = InvalidIdentifier(label);
        return false;
    }

    private static string InvalidIdentifier(string label) =>
        $"{label} must start with a lowercase letter or underscore and contain only lowercase letters, digits and underscores (max {MaxIdentifierLength} characters).";
}
