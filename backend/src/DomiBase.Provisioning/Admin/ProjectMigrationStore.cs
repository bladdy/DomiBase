using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Provisioning.Admin;

/// <summary>
/// Stores the versioned migration history inside each project database, in a private schema
/// that the Runtime and the schema inspector never expose. The whole migration (DDL plus its
/// history row) is committed as one transaction while acting as the project owner.
/// </summary>
public sealed class ProjectMigrationStore(
    IProvisioningConnectionFactory connectionFactory,
    ILogger<ProjectMigrationStore> logger) : IMigrationStore
{
    private const string HistorySchema = "domibase_migrations";
    private const string HistoryTable = HistorySchema + ".applied";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<MigrationApplyOutcome> ApplyAsync(
        ProjectResourceNames names,
        MigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ProjectSchemaAdmin.ExecuteAsync(
                connection, transaction, $"SET LOCAL ROLE {Q(names.OwnerRole)}", cancellationToken);

            // Serializes concurrent migrations for this database for the lifetime of the transaction.
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext(@database))", connection, transaction)
                {
                    CommandTimeout = (int)CommandTimeout.TotalSeconds
                })
            {
                lockCommand.Parameters.AddWithValue("database", names.DatabaseName);
                await lockCommand.ExecuteScalarAsync(cancellationToken);
            }

            await EnsureHistoryTableAsync(connection, transaction, cancellationToken);

            var existing = await FindAsync(connection, transaction, plan.Version, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.Checksum, plan.Checksum, StringComparison.Ordinal)
                    || !string.Equals(existing.Name, plan.Name, StringComparison.Ordinal))
                {
                    throw new SchemaAdminException(
                        "That migration version is already applied with a different definition.",
                        "Use a new version instead of changing an applied migration.");
                }

                return new MigrationApplyOutcome(existing, true);
            }

            var highestVersion = await HighestVersionAsync(connection, transaction, cancellationToken);
            if (plan.Version <= highestVersion)
            {
                throw new SchemaAdminException(
                    "Migration versions must be applied in increasing order.",
                    $"The highest applied version is {highestVersion}.");
            }

            foreach (var operation in plan.Operations)
            {
                if (operation is DropIndexMutation dropIndex
                    && !await ProjectSchemaAdmin.IndexBelongsToTableAsync(
                        connection, transaction, dropIndex, cancellationToken))
                {
                    throw new SchemaAdminException(
                        "The index does not exist on this table.",
                        "Check the table and index names.",
                        true);
                }

                foreach (var statement in ProjectSchemaAdmin.BuildStatements(names, operation))
                {
                    await ProjectSchemaAdmin.ExecuteAsync(
                        connection, transaction, statement, cancellationToken);
                }
            }

            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {HistoryTable} (version, name, checksum, operation_count)
                VALUES (@version, @name, @checksum, @operations)
                """,
                connection,
                transaction)
                {
                    CommandTimeout = (int)CommandTimeout.TotalSeconds
                })
            {
                insert.Parameters.AddWithValue("version", plan.Version);
                insert.Parameters.AddWithValue("name", plan.Name);
                insert.Parameters.AddWithValue("checksum", plan.Checksum);
                insert.Parameters.AddWithValue("operations", plan.Operations.Count);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(
                "Migration {Version} rejected for database {DatabaseName}: {SqlState}",
                plan.Version,
                names.DatabaseName,
                exception.SqlState);
            throw ProjectSchemaAdmin.Translate(exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Migration {Version} applied for database {DatabaseName}.",
            plan.Version,
            names.DatabaseName);

        return new MigrationApplyOutcome(
            new AppliedMigration(
                plan.Version,
                plan.Name,
                plan.Checksum,
                plan.Operations.Count,
                DateTimeOffset.UtcNow),
            false);
    }

    public async Task<IReadOnlyList<AppliedMigration>> ListAsync(
        ProjectResourceNames names,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ProjectSchemaAdmin.ExecuteAsync(
                connection, transaction, $"SET LOCAL ROLE {Q(names.OwnerRole)}", cancellationToken);

            await using var existsCommand = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection, transaction)
            {
                CommandTimeout = (int)CommandTimeout.TotalSeconds
            };
            existsCommand.Parameters.AddWithValue("name", HistoryTable);
            if (await existsCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return [];
            }

            await using var command = new NpgsqlCommand(
                $"""
                SELECT version, name, checksum, operation_count, applied_at
                FROM {HistoryTable}
                ORDER BY version
                """,
                connection,
                transaction)
            {
                CommandTimeout = (int)CommandTimeout.TotalSeconds
            };

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var migrations = new List<AppliedMigration>();
            while (await reader.ReadAsync(cancellationToken))
            {
                migrations.Add(new AppliedMigration(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }

            return migrations;
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(
                "Migration history listing rejected for database {DatabaseName}: {SqlState}",
                names.DatabaseName,
                exception.SqlState);
            throw ProjectSchemaAdmin.Translate(exception);
        }
    }

    public string Describe() => "project-migration-history";

    private static async Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ProjectSchemaAdmin.ExecuteAsync(
            connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {Q(HistorySchema)}", cancellationToken);

        await ProjectSchemaAdmin.ExecuteAsync(
            connection,
            transaction,
            $"""
            CREATE TABLE IF NOT EXISTS {HistoryTable} (
                version int4 PRIMARY KEY,
                name text NOT NULL,
                checksum text NOT NULL,
                operation_count int4 NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """,
            cancellationToken);
    }

    private static async Task<AppliedMigration?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT version, name, checksum, operation_count, applied_at
            FROM {HistoryTable}
            WHERE version = @version
            """,
            connection,
            transaction)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };
        command.Parameters.AddWithValue("version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AppliedMigration(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static async Task<int> HighestVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(max(version), 0) FROM {HistoryTable}",
            connection,
            transaction)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Q(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
