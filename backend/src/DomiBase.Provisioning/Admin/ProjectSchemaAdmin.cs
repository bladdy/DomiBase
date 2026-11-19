using System.Data;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Provisioning.Admin;

/// <summary>
/// Executes validated schema administration inside the target project database. It opens
/// the privileged provisioning connection, switches to the project owner with a
/// transaction-scoped <c>SET LOCAL ROLE</c>, and commits or rolls back as a unit. Only the
/// Application layer's <see cref="SchemaDdlValidator"/> output reaches this class, so every
/// identifier and literal is safe to quote. No PostgreSQL role name is accepted from clients.
/// </summary>
public sealed class ProjectSchemaAdmin(
    IProvisioningConnectionFactory connectionFactory,
    ILogger<ProjectSchemaAdmin> logger) : IProjectSchemaAdmin
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> CommandPrivileges = new(StringComparer.Ordinal)
    {
        [nameof(PolicyCommand.Select)] = "SELECT",
        [nameof(PolicyCommand.Insert)] = "INSERT",
        [nameof(PolicyCommand.Update)] = "UPDATE",
        [nameof(PolicyCommand.Delete)] = "DELETE"
    };

    public async Task ExecuteAsync(
        ProjectResourceNames names,
        SchemaMutation mutation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {Q(names.OwnerRole)}", cancellationToken);

            if (mutation is DropIndexMutation dropIndex
                && !await IndexBelongsToTableAsync(connection, transaction, dropIndex, cancellationToken))
            {
                throw new SchemaAdminException(
                    "The index does not exist on this table.",
                    "Check the table and index names.",
                    true);
            }

            var statements = BuildStatements(names, mutation);
            foreach (var statement in statements)
            {
                await ExecuteAsync(connection, transaction, statement, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(
                "Schema operation {Action} rejected for database {DatabaseName}: {SqlState}",
                mutation.AuditAction,
                names.DatabaseName,
                exception.SqlState);
            throw Translate(exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Schema operation {Action} completed for database {DatabaseName}.",
            mutation.AuditAction,
            names.DatabaseName);
    }

    public async Task<IReadOnlyList<SchemaPolicy>> ListPoliciesAsync(
        ProjectResourceNames names,
        string table,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);

        try
        {
            var policyRows = await QueryAsync(
                connection,
                """
                SELECT policyname::text, qual::text, with_check::text, roles::text[]
                FROM pg_policies
                WHERE schemaname = 'public' AND tablename = @table
                ORDER BY policyname
                """,
                command => command.Parameters.AddWithValue("table", table),
                reader => (
                    Name: reader.GetString(0),
                    Using: reader.IsDBNull(1) ? null : reader.GetString(1),
                    WithCheck: reader.IsDBNull(2) ? null : reader.GetString(2),
                    Roles: reader.GetFieldValue<string[]>(3)),
                cancellationToken);

            var grants = await QueryAsync(
                connection,
                """
                SELECT grantee::text, privilege_type::text
                FROM information_schema.role_table_grants
                WHERE table_schema = 'public' AND table_name = @table
                  AND grantee IN (@anon, @authenticated)
                """,
                command =>
                {
                    command.Parameters.AddWithValue("table", table);
                    command.Parameters.AddWithValue("anon", names.AnonymousRole);
                    command.Parameters.AddWithValue("authenticated", names.AuthenticatedRole);
                },
                reader => (Role: reader.GetString(0), Privilege: reader.GetString(1)),
                cancellationToken);

            var commandsByRole = grants
                .Where(grant => CommandPrivileges.Values.Contains(grant.Privilege, StringComparer.Ordinal))
                .GroupBy(grant => ToLogicalRole(grant.Role, names))
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(grant => grant.Privilege).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);

            return policyRows
                .Select(row => new SchemaPolicy(
                    row.Name,
                    commandsByRole.Values.SelectMany(value => value).Distinct(StringComparer.Ordinal).ToArray(),
                    row.Roles.Select(role => ToLogicalRole(role, names)).Distinct(StringComparer.Ordinal).ToArray(),
                    row.Using,
                    row.WithCheck))
                .OrderBy(policy => policy.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (PostgresException exception)
        {
            logger.LogWarning(
                "Policy listing rejected for database {DatabaseName}: {SqlState}",
                names.DatabaseName,
                exception.SqlState);
            throw Translate(exception);
        }
    }

    public string Describe() => "project-owned-ddl";

    internal static IReadOnlyList<string> BuildStatements(ProjectResourceNames names, SchemaMutation mutation)
    {
        var statements = new List<string>();

        switch (mutation)
        {
            case CreateTableMutation create:
            {
                var columns = create.Columns.Select(DescribeColumn).Select(part => "    " + part);
                statements.Add($"CREATE TABLE {T(create.Table)} (\n{string.Join(",\n", columns)}\n)");
                statements.Add($"ALTER TABLE {T(create.Table)} ENABLE ROW LEVEL SECURITY");
                break;
            }

            case DropTableMutation drop:
                statements.Add($"DROP TABLE IF EXISTS {T(drop.Table)}");
                break;

            case AddColumnMutation add:
                statements.Add($"ALTER TABLE {T(add.Table)} ADD COLUMN {DescribeColumn(add.Column)}");
                break;

            case AlterColumnMutation alter:
            {
                if (alter.NewName is not null)
                {
                    statements.Add(
                        $"ALTER TABLE {T(alter.Table)} RENAME COLUMN {Q(alter.Column)} TO {Q(alter.NewName)}");
                }

                var current = alter.NewName ?? alter.Column;
                if (alter.Type is not null)
                {
                    statements.Add(
                        $"ALTER TABLE {T(alter.Table)} ALTER COLUMN {Q(current)} TYPE {alter.Type} " +
                        $"USING {Q(current)}::{alter.Type}");
                }

                if (alter.Nullable is not null)
                {
                    statements.Add(
                        $"ALTER TABLE {T(alter.Table)} ALTER COLUMN {Q(current)} " +
                        (alter.Nullable.Value ? "DROP NOT NULL" : "SET NOT NULL"));
                }

                break;
            }

            case DropColumnMutation dropColumn:
                statements.Add($"ALTER TABLE {T(dropColumn.Table)} DROP COLUMN IF EXISTS {Q(dropColumn.Column)}");
                break;

            case CreateIndexMutation createIndex:
            {
                var columns = string.Join(", ", createIndex.Columns.Select(Q));
                statements.Add(
                    $"CREATE {(createIndex.Unique ? "UNIQUE " : string.Empty)}INDEX {Q(createIndex.Name)} " +
                    $"ON {T(createIndex.Table)} ({columns})");
                break;
            }

            case DropIndexMutation dropIndex:
                statements.Add($"DROP INDEX IF EXISTS {Q(dropIndex.Name)}");
                break;

            case CreatePolicyMutation createPolicy:
                ApplyPolicy(statements, names, createPolicy.Table, createPolicy.Name, createPolicy.Commands,
                    createPolicy.Roles, createPolicy.Using, createPolicy.WithCheck);
                break;

            case AlterPolicyMutation alterPolicy:
                statements.Add($"DROP POLICY IF EXISTS {Q(alterPolicy.Name)} ON {T(alterPolicy.Table)}");
                ApplyPolicy(statements, names, alterPolicy.Table, alterPolicy.Name, alterPolicy.Commands,
                    alterPolicy.Roles, alterPolicy.Using, alterPolicy.WithCheck);
                break;

            case DropPolicyMutation dropPolicy:
                statements.Add($"DROP POLICY IF EXISTS {Q(dropPolicy.Name)} ON {T(dropPolicy.Table)}");
                break;

            default:
                throw new SchemaAdminException("Unsupported schema operation.");
        }

        return statements;
    }

    private static void ApplyPolicy(
        ICollection<string> statements,
        ProjectResourceNames names,
        string table,
        string name,
        IReadOnlyList<PolicyCommand> commands,
        IReadOnlyList<SchemaFunctionalRole> roles,
        string usingExpression,
        string withCheckExpression)
    {
        var functionalRoles = roles.Select(role => ToPhysicalRole(role, names)).ToArray();
        var roleList = string.Join(", ", functionalRoles.Select(Q));
        var privileges = string.Join(", ", commands.Select(command => CommandPrivileges[command.ToString()]));

        statements.Add($"ALTER TABLE {T(table)} ENABLE ROW LEVEL SECURITY");

        // Grants are additive and limited to this policy's commands. Previously revoking ALL
        // here silently removed the grant of an existing complementary policy (for example
        // anon_select when anon_insert was created), breaking the Table Editor toggles.
        // RLS remains the definitive gate: a policy without a matching grant or a grant
        // without a policy yields no access.
        statements.Add($"GRANT {privileges} ON TABLE {T(table)} TO {roleList}");
        statements.Add(
            $"CREATE POLICY {Q(name)} ON {T(table)} AS PERMISSIVE FOR ALL TO {roleList} " +
            $"USING ({usingExpression}) WITH CHECK ({withCheckExpression})");
    }

    private static string DescribeColumn(ColumnDefinition column)
    {
        var parts = new List<string> { Q(column.Name), column.Type };

        if (!column.Nullable)
        {
            parts.Add("NOT NULL");
        }

        if (column.Identity)
        {
            parts.Add("GENERATED BY DEFAULT AS IDENTITY");
        }
        else if (column.DefaultSql is not null)
        {
            parts.Add("DEFAULT " + column.DefaultSql);
        }

        if (column.PrimaryKey)
        {
            parts.Add("PRIMARY KEY");
        }
        else if (column.Unique)
        {
            parts.Add("UNIQUE");
        }

        if (column.Reference is not null)
        {
            parts.Add($"REFERENCES {T(column.Reference.Table)}({Q(column.Reference.Column)})");
        }

        return string.Join(" ", parts);
    }

    private static string ToPhysicalRole(SchemaFunctionalRole role, ProjectResourceNames names) =>
        role switch
        {
            SchemaFunctionalRole.Anon => names.AnonymousRole,
            SchemaFunctionalRole.Authenticated => names.AuthenticatedRole,
            _ => throw new SchemaAdminException("Unsupported functional role.")
        };

    private static string ToLogicalRole(string physicalRole, ProjectResourceNames names)
    {
        if (string.Equals(physicalRole, names.AnonymousRole, StringComparison.Ordinal))
        {
            return "anon";
        }

        if (string.Equals(physicalRole, names.AuthenticatedRole, StringComparison.Ordinal))
        {
            return "authenticated";
        }

        return physicalRole;
    }

    /// <summary>
    /// Drops are restricted to indexes that actually belong to the requested table. Index
    /// names are schema-scoped in PostgreSQL, so this guard prevents a guessed name from
    /// removing an unrelated object.
    /// </summary>
    internal static async Task<bool> IndexBelongsToTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DropIndexMutation mutation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM pg_index i
            JOIN pg_class idx ON idx.oid = i.indexrelid
            JOIN pg_class tbl ON tbl.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = tbl.relnamespace
            WHERE n.nspname = 'public' AND tbl.relname = @table AND idx.relname = @index
            LIMIT 1
            """,
            connection,
            transaction)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };
        command.Parameters.AddWithValue("table", mutation.Table);
        command.Parameters.AddWithValue("index", mutation.Name);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> configure,
        Func<NpgsqlDataReader, T> project,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };
        configure(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(project(reader));
        }

        return results;
    }

    internal static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static SchemaAdminException Translate(PostgresException exception) => exception.SqlState switch
    {
        "42P07" or "42710" or "42P06" => new SchemaAdminException(
            "An object with that name already exists.",
            "Choose a different name or remove the existing object first.",
            true),
        "42P01" => new SchemaAdminException(
            "The table does not exist.",
            "Create the table before applying this change.",
            true),
        "42703" => new SchemaAdminException(
            "The column does not exist.",
            "Check the column name.",
            true),
        "42501" => new SchemaAdminException(
            "The schema operation is not permitted for this project."),
        _ => new SchemaAdminException(
            "The database rejected the schema operation.",
            $"PostgreSQL state {exception.SqlState}.")
    };

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string T(string table) => $"public.{Q(table)}";
}
