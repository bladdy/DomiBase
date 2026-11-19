using System.Data;
using System.Text.RegularExpressions;
using DomiBase.Application.Schema;
using Npgsql;

namespace DomiBase.Provisioning.Catalog;

/// <summary>
/// Read-only PostgreSQL catalog queries for the Control Plane schema inspector.
/// Uses the privileged provisioning connection string but never exposes it, opens a
/// new connection per call and forces <c>SET TRANSACTION READ ONLY</c> so a bug in the
/// inspector can never mutate a project database. The inspected schema is fixed by the
/// application layer; only that constant is parameterized in every query.
/// </summary>
public sealed class ProjectCatalogReader(ProvisioningOptions options) : IProjectCatalogReader
{
    private static readonly Regex DatabaseNamePattern =
        new(@"^domibase_[a-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string TablesSql = """
        SELECT c.relname::text AS name
        FROM pg_catalog.pg_class AS c
        JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema
          AND c.relkind IN ('r', 'p')
        ORDER BY c.relname;
        """;

    private const string ViewsSql = """
        SELECT c.relname::text AS name
        FROM pg_catalog.pg_class AS c
        JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema
          AND c.relkind IN ('v', 'm')
        ORDER BY c.relname;
        """;

    private const string FunctionsSql = """
        SELECT p.proname::text AS name,
               pg_catalog.pg_get_function_identity_arguments(p.oid) AS arguments,
               pg_catalog.pg_get_function_result(p.oid) AS return_type
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema
        ORDER BY p.proname, arguments;
        """;

    private const string ColumnsSql = """
        SELECT col.column_name::text,
               col.udt_name::text,
               col.data_type::text,
               (col.is_nullable = 'YES') AS is_nullable,
               col.column_default::text,
               (col.identity_generation IS NOT NULL) AS is_identity,
               EXISTS (
                   SELECT 1
                   FROM information_schema.table_constraints tc
                   JOIN information_schema.key_column_usage kcu
                     ON kcu.constraint_catalog = tc.constraint_catalog
                    AND kcu.constraint_schema = tc.constraint_schema
                    AND kcu.constraint_name = tc.constraint_name
                   WHERE tc.table_schema = col.table_schema
                     AND tc.table_name = col.table_name
                     AND kcu.column_name = col.column_name
                     AND tc.constraint_type = 'PRIMARY KEY'
               ) AS is_primary_key,
               EXISTS (
                   SELECT 1
                   FROM information_schema.table_constraints tc
                   JOIN information_schema.key_column_usage kcu
                     ON kcu.constraint_catalog = tc.constraint_catalog
                    AND kcu.constraint_schema = tc.constraint_schema
                    AND kcu.constraint_name = tc.constraint_name
                   WHERE tc.table_schema = col.table_schema
                     AND tc.table_name = col.table_name
                     AND kcu.column_name = col.column_name
                     AND tc.constraint_type = 'UNIQUE'
               ) AS is_unique,
               fk.foreign_table,
               fk.foreign_column
        FROM information_schema.columns col
        LEFT JOIN LATERAL (
            SELECT ccu.table_name::text AS foreign_table,
                   ccu.column_name::text AS foreign_column
            FROM information_schema.key_column_usage kcu
            JOIN information_schema.referential_constraints rc
              ON rc.constraint_catalog = kcu.constraint_catalog
             AND rc.constraint_schema = kcu.constraint_schema
             AND rc.constraint_name = kcu.constraint_name
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_catalog = rc.unique_constraint_catalog
             AND ccu.constraint_schema = rc.unique_constraint_schema
             AND ccu.constraint_name = rc.unique_constraint_name
            WHERE kcu.table_schema = col.table_schema
              AND kcu.table_name = col.table_name
              AND kcu.column_name = col.column_name
            LIMIT 1
        ) fk ON true
        WHERE col.table_schema = @schema
          AND col.table_name = @table
        ORDER BY col.ordinal_position;
        """;

    public Task<IReadOnlyList<SchemaTable>> ListTablesAsync(
        string database,
        CancellationToken cancellationToken = default) =>
        ReadAsync(database, TablesSql, reader => new SchemaTable(reader.GetString(0)), cancellationToken);

    public Task<IReadOnlyList<SchemaView>> ListViewsAsync(
        string database,
        CancellationToken cancellationToken = default) =>
        ReadAsync(database, ViewsSql, reader => new SchemaView(reader.GetString(0)), cancellationToken);

    public Task<IReadOnlyList<SchemaFunction>> ListFunctionsAsync(
        string database,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            database,
            FunctionsSql,
            reader => new SchemaFunction(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            cancellationToken);

    public async Task<IReadOnlyList<SchemaColumn>> ListColumnsAsync(
        string database,
        string table,
        CancellationToken cancellationToken = default)
    {
        if (!DatabaseNamePattern.IsMatch(database))
        {
            throw new ArgumentException("The database name is not a valid DomiBase project database.", nameof(database));
        }

        if (!SchemaDdlValidator.IsIdentifier(table))
        {
            throw new ArgumentException("The table name is not a valid exposed identifier.", nameof(table));
        }

        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            Database = database,
            ApplicationName = "domibase-catalog"
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = new NpgsqlCommand(ColumnsSql, connection, transaction);
        command.Parameters.AddWithValue("schema", SchemaService.ExposedSchema);
        command.Parameters.AddWithValue("table", table);

        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<SchemaColumn>();
        while (await result.ReadAsync(cancellationToken))
        {
            columns.Add(new SchemaColumn(
                result.GetString(0),
                result.GetString(1),
                result.GetString(2),
                result.GetBoolean(3),
                result.IsDBNull(4) ? null : result.GetString(4),
                result.GetBoolean(6),
                result.GetBoolean(7),
                result.GetBoolean(5),
                result.IsDBNull(8) ? null : result.GetString(8),
                result.IsDBNull(9) ? null : result.GetString(9)));
        }

        await result.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return columns;
    }

    public string Describe() => "project-catalog:read-only";

    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        string database,
        string sql,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        if (!DatabaseNamePattern.IsMatch(database))
        {
            throw new ArgumentException("The database name is not a valid DomiBase project database.", nameof(database));
        }

        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            Database = database,
            ApplicationName = "domibase-catalog"
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", SchemaService.ExposedSchema);

        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<T>();
        while (await result.ReadAsync(cancellationToken))
        {
            objects.Add(map(result));
        }

        await result.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return objects;
    }
}
