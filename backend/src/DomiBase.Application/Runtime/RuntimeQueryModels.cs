namespace DomiBase.Application.Runtime;

/// <summary>
/// Column metadata exposed to the Runtime query builder. Privileges are derived from the
/// project catalog for the functional request role, so the builder never emits a column
/// the database would reject.
/// </summary>
public sealed record RuntimeColumn(
    string Name,
    string UdtName,
    bool CanSelect,
    bool CanInsert,
    bool CanUpdate)
{
    public bool IsText => UdtName is "text" or "varchar" or "bpchar" or "char" or "name" or "citext";

    public bool IsBoolean => UdtName == "bool";
}

/// <summary>
/// Catalog-derived privileges for one project table. Tables without any grant, or without
/// row-level security enabled, are never exposed by the Runtime.
/// </summary>
public sealed record RuntimeTableMetadata(
    string Name,
    bool CanSelect,
    bool CanInsert,
    bool CanUpdate,
    bool CanDelete,
    IReadOnlyList<RuntimeColumn> Columns)
{
    public RuntimeColumn? FindColumn(string name) =>
        Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal));
}

/// <summary>A text parameter; the generated SQL casts it to the column type.</summary>
public sealed record RuntimeParameter(string Name, string? Value);

/// <summary>Fully parameterized SQL plus the columns returned by <c>RETURNING</c>.</summary>
public sealed record RuntimeSql(
    string CommandText,
    IReadOnlyList<RuntimeParameter> Parameters,
    IReadOnlyList<string> ResultColumns);

public sealed record RuntimeReadOptions(
    IReadOnlyList<string>? Select,
    IReadOnlyList<string> Filters,
    string? Order,
    int? Limit,
    int? Offset);

public sealed record RuntimeWriteOptions(
    IReadOnlyList<string>? Select,
    IReadOnlyList<string> Filters);
