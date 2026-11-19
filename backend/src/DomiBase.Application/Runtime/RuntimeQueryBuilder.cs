using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DomiBase.Application.Runtime;

/// <summary>
/// Builds parameterized SQL for the Runtime API. Identifiers come exclusively from
/// catalog metadata and are quoted; values are always parameters cast to the column type.
/// No client-provided SQL fragment ever reaches PostgreSQL.
/// </summary>
public static partial class RuntimeQueryBuilder
{
    private const int MaxSelectColumns = 50;
    private const int MaxFilters = 10;
    private const int MaxInValues = 100;
    private const int MaxOrderColumns = 3;
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;
    private const int MaxOffset = 1_000_000;

    private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "like", "ilike", "in", "is"
    };

    public static RuntimeSql BuildSelect(RuntimeTableMetadata table, RuntimeReadOptions options)
    {
        EnsureExposed(table.CanSelect, table.Name);

        var columns = ResolveColumns(table, options.Select);
        var parameters = new List<RuntimeParameter>();
        var conditions = BuildWhere(table, options.Filters, parameters);
        var order = BuildOrder(table, options.Order);

        var limit = options.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
        {
            throw BadRequest("INVALID_LIMIT", $"Limit must be between 1 and {MaxLimit}.");
        }

        var offset = options.Offset ?? 0;
        if (offset is < 0 or > MaxOffset)
        {
            throw BadRequest("INVALID_OFFSET", $"Offset must be between 0 and {MaxOffset}.");
        }

        var sql = new StringBuilder("SELECT ");
        AppendIdentifierList(sql, columns);
        sql.Append(" FROM ").Append(TableReference(table.Name));

        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        }

        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order));
        }

        sql.Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        sql.Append(" OFFSET ").Append(offset.ToString(CultureInfo.InvariantCulture));

        return new RuntimeSql(sql.ToString(), parameters, columns);
    }

    public static RuntimeSql BuildInsert(
        RuntimeTableMetadata table,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string>? returning)
    {
        EnsureExposed(table.CanInsert, table.Name);

        if (values.Count == 0)
        {
            throw BadRequest("INVALID_BODY", "At least one field is required.");
        }

        var parameters = new List<RuntimeParameter>();
        var names = new List<string>(values.Count);
        var expressions = new List<string>(values.Count);

        foreach (var (name, value) in values)
        {
            var column = RequireColumn(table, name, RuntimeColumnAccess.Insert);
            names.Add(Quote(column.Name));
            expressions.Add(BuildValueExpression(column, value, parameters));
        }

        var resultColumns = ResolveColumns(table, returning);

        var sql = new StringBuilder("INSERT INTO ");
        sql.Append(TableReference(table.Name)).Append(" (");
        sql.Append(string.Join(", ", names));
        sql.Append(") VALUES (");
        sql.Append(string.Join(", ", expressions));
        sql.Append(')');

        AppendReturning(sql, resultColumns);

        return new RuntimeSql(sql.ToString(), parameters, resultColumns);
    }

    public static RuntimeSql BuildUpdate(
        RuntimeTableMetadata table,
        IReadOnlyDictionary<string, string?> values,
        RuntimeWriteOptions options)
    {
        EnsureExposed(table.CanUpdate, table.Name);

        if (values.Count == 0)
        {
            throw BadRequest("INVALID_BODY", "At least one field is required.");
        }

        var parameters = new List<RuntimeParameter>();
        var assignments = new List<string>(values.Count);

        foreach (var (name, value) in values)
        {
            var column = RequireColumn(table, name, RuntimeColumnAccess.Update);
            assignments.Add($"{Quote(column.Name)} = {BuildValueExpression(column, value, parameters)}");
        }

        var conditions = BuildWhere(table, options.Filters, parameters);
        if (conditions.Count == 0)
        {
            throw BadRequest(
                "FILTER_REQUIRED",
                "At least one filter is required for updates.",
                "Use ?f=column.operator.value.");
        }

        var resultColumns = ResolveColumns(table, options.Select);

        var sql = new StringBuilder("UPDATE ");
        sql.Append(TableReference(table.Name));
        sql.Append(" SET ").Append(string.Join(", ", assignments));
        sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        AppendReturning(sql, resultColumns);

        return new RuntimeSql(sql.ToString(), parameters, resultColumns);
    }

    public static RuntimeSql BuildDelete(RuntimeTableMetadata table, RuntimeWriteOptions options)
    {
        EnsureExposed(table.CanDelete, table.Name);

        var parameters = new List<RuntimeParameter>();
        var conditions = BuildWhere(table, options.Filters, parameters);
        if (conditions.Count == 0)
        {
            throw BadRequest(
                "FILTER_REQUIRED",
                "At least one filter is required for deletes.",
                "Use ?f=column.operator.value.");
        }

        var resultColumns = ResolveColumns(table, options.Select);

        var sql = new StringBuilder("DELETE FROM ");
        sql.Append(TableReference(table.Name));
        sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        AppendReturning(sql, resultColumns);

        return new RuntimeSql(sql.ToString(), parameters, resultColumns);
    }

    private static IReadOnlyList<string> ResolveColumns(
        RuntimeTableMetadata table,
        IReadOnlyList<string>? requested)
    {
        if (!table.CanSelect)
        {
            return [];
        }

        if (requested is null || requested.Count == 0)
        {
            return table.Columns
                .Where(column => column.CanSelect)
                .Select(column => column.Name)
                .ToList();
        }

        if (requested.Count > MaxSelectColumns)
        {
            throw BadRequest(
                "INVALID_SELECT",
                $"At most {MaxSelectColumns} columns can be selected.");
        }

        var result = new List<string>(requested.Count);
        foreach (var name in requested)
        {
            var column = RequireColumn(table, name, RuntimeColumnAccess.Select);
            if (result.Contains(column.Name, StringComparer.Ordinal))
            {
                throw BadRequest("INVALID_SELECT", $"Column '{column.Name}' is duplicated.");
            }

            result.Add(column.Name);
        }

        return result;
    }

    private static List<string> BuildWhere(
        RuntimeTableMetadata table,
        IReadOnlyList<string>? filters,
        List<RuntimeParameter> parameters)
    {
        var conditions = new List<string>();
        if (filters is null)
        {
            return conditions;
        }

        if (filters.Count > MaxFilters)
        {
            throw BadRequest("INVALID_FILTER", $"At most {MaxFilters} filters are allowed.");
        }

        foreach (var raw in filters)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw BadRequest(
                    "INVALID_FILTER",
                    "Filter must use column.operator.value.",
                    "Example: ?f=estado.eq.nuevo.");
            }

            var first = raw.IndexOf('.', StringComparison.Ordinal);
            var second = first < 0 ? -1 : raw.IndexOf('.', first + 1);

            if (first <= 0 || second <= first + 1)
            {
                throw BadRequest(
                    "INVALID_FILTER",
                    $"Filter '{raw}' is invalid.",
                    "Use column.operator.value, for example estado.eq.nuevo.");
            }

            var columnName = raw[..first];
            var op = raw[(first + 1)..second];
            var value = raw[(second + 1)..];

            if (!AllowedOperators.Contains(op))
            {
                throw BadRequest("INVALID_FILTER_OPERATOR", $"Operator '{op}' is not allowed.");
            }

            var column = RequireColumn(table, columnName, RuntimeColumnAccess.Select);

            if (op == "is")
            {
                conditions.Add(BuildIsCondition(column, value));
                continue;
            }

            var cast = CastFor(column.UdtName);
            if (cast is null)
            {
                throw UnsupportedColumnType(column);
            }

            if (op is "like" or "ilike")
            {
                if (!column.IsText)
                {
                    throw BadRequest(
                        "INVALID_FILTER_OPERATOR",
                        $"Operator '{op}' requires a text column.",
                        $"Column '{column.Name}' is not text.");
                }

                conditions.Add(
                    $"{Quote(column.Name)} {OperatorSql(op)} {AddParameter(parameters, value)}::text");
                continue;
            }

            if (op == "in")
            {
                var items = value.Split(',', StringSplitOptions.None);
                if (items.Length > MaxInValues)
                {
                    throw BadRequest("INVALID_FILTER", $"At most {MaxInValues} values are allowed for 'in'.");
                }

                var placeholders = items
                    .Select(item => $"{AddParameter(parameters, item)}::{cast}")
                    .ToList();

                conditions.Add($"{Quote(column.Name)} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            conditions.Add(
                $"{Quote(column.Name)} {OperatorSql(op)} {AddParameter(parameters, value)}::{cast}");
        }

        return conditions;
    }

    private static string BuildIsCondition(RuntimeColumn column, string value)
    {
        var normalized = value.Trim().ToLowerInvariant();

        if (normalized == "null")
        {
            return $"{Quote(column.Name)} IS NULL";
        }

        if (normalized is "true" or "false")
        {
            if (!column.IsBoolean)
            {
                throw BadRequest(
                    "INVALID_FILTER_VALUE",
                    $"Column '{column.Name}' is not boolean.",
                    "Use is.null for null checks or is.true/is.false for booleans.");
            }

            return $"{Quote(column.Name)} IS {(normalized == "true" ? "TRUE" : "FALSE")}";
        }

        throw BadRequest(
            "INVALID_FILTER_VALUE",
            "The 'is' operator only accepts null, true or false.");
    }

    private static RuntimeColumn RequireColumn(
        RuntimeTableMetadata table,
        string name,
        RuntimeColumnAccess access)
    {
        if (!IsSafeIdentifier().IsMatch(name))
        {
            throw BadRequest("INVALID_COLUMN", $"Column '{name}' is invalid.");
        }

        var column = table.FindColumn(name)
            ?? throw BadRequest("INVALID_COLUMN", $"Column '{name}' is not available.");

        var allowed = access switch
        {
            RuntimeColumnAccess.Select => column.CanSelect,
            RuntimeColumnAccess.Insert => column.CanInsert,
            RuntimeColumnAccess.Update => column.CanUpdate,
            _ => false
        };

        if (!allowed)
        {
            // Same message as an unknown column: do not disclose the project schema.
            throw BadRequest("INVALID_COLUMN", $"Column '{name}' is not available.");
        }

        return column;
    }

    private enum RuntimeColumnAccess
    {
        Select,
        Insert,
        Update
    }

    private static List<string> BuildOrder(RuntimeTableMetadata table, string? order)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(order))
        {
            return result;
        }

        var parts = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > MaxOrderColumns)
        {
            throw BadRequest("INVALID_ORDER", $"At most {MaxOrderColumns} order columns are allowed.");
        }

        foreach (var part in parts)
        {
            var separator = part.IndexOf('.', StringComparison.Ordinal);
            var columnName = separator < 0 ? part : part[..separator];
            var direction = separator < 0 ? "asc" : part[(separator + 1)..].ToLowerInvariant();

            if (direction is not ("asc" or "desc"))
            {
                throw BadRequest("INVALID_ORDER", $"Order direction '{direction}' is invalid.");
            }

            var column = RequireColumn(table, columnName, RuntimeColumnAccess.Select);
            result.Add($"{Quote(column.Name)} {(direction == "asc" ? "ASC" : "DESC")}");
        }

        return result;
    }

    private static string BuildValueExpression(
        RuntimeColumn column,
        string? value,
        List<RuntimeParameter> parameters)
    {
        if (value is null)
        {
            return "NULL";
        }

        var cast = CastFor(column.UdtName)
            ?? throw UnsupportedColumnType(column);

        return $"{AddParameter(parameters, value)}::{cast}";
    }

    private static string AddParameter(List<RuntimeParameter> parameters, string? value)
    {
        var name = "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new RuntimeParameter(name, value));
        return "@" + name;
    }

    private static void AppendReturning(StringBuilder sql, IReadOnlyList<string> resultColumns)
    {
        if (resultColumns.Count == 0)
        {
            return;
        }

        sql.Append(" RETURNING ");
        AppendIdentifierList(sql, resultColumns);
    }

    private static void AppendIdentifierList(StringBuilder sql, IReadOnlyList<string> columns) =>
        sql.Append(string.Join(", ", columns.Select(Quote)));

    private static string TableReference(string table) =>
        Quote("public") + "." + Quote(table);

    private static string OperatorSql(string op) => op switch
    {
        "eq" => "=",
        "neq" => "<>",
        "gt" => ">",
        "gte" => ">=",
        "lt" => "<",
        "lte" => "<=",
        "like" => "LIKE",
        "ilike" => "ILIKE",
        _ => throw new InvalidOperationException($"Unsupported operator '{op}'.")
    };

    /// <summary>PostgreSQL type used to cast a text parameter derived from catalog metadata.</summary>
    private static string? CastFor(string udtName) => udtName switch
    {
        "text" or "varchar" or "bpchar" or "char" or "name" or "citext" => "text",
        "int2" => "int2",
        "int4" => "int4",
        "int8" => "int8",
        "numeric" => "numeric",
        "float4" => "float4",
        "float8" => "float8",
        "bool" => "bool",
        "uuid" => "uuid",
        "date" => "date",
        "timestamp" => "timestamp",
        "timestamptz" => "timestamptz",
        "time" => "time",
        "timetz" => "timetz",
        "interval" => "interval",
        "json" => "json",
        "jsonb" => "jsonb",
        "inet" => "inet",
        "cidr" => "cidr",
        "macaddr" => "macaddr",
        "macaddr8" => "macaddr8",
        _ => null
    };

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static void EnsureExposed(bool allowed, string table)
    {
        if (!allowed)
        {
            throw new RuntimeQueryException(
                404,
                "TABLE_NOT_FOUND",
                $"Table '{table}' is not exposed for this operation.");
        }
    }

    private static RuntimeQueryException UnsupportedColumnType(RuntimeColumn column) =>
        BadRequest(
            "UNSUPPORTED_COLUMN_TYPE",
            $"Column '{column.Name}' uses a type that is not supported for filters or writes.");

    private static RuntimeQueryException BadRequest(string code, string message, string? hint = null) =>
        new(400, code, message, hint);

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IsSafeIdentifier();
}
