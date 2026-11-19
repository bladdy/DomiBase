using DomiBase.Application.Backups;
using DomiBase.Application.Projects;
using Npgsql;

namespace DomiBase.Provisioning;

/// <summary>
/// Implements the backup/restore coordination contract with the same privileged connection
/// factory and advisory-lock key used by provisioning, so the two flows cannot overlap.
/// </summary>
public sealed class ProjectDatabaseCoordinator(
    IProvisioningConnectionFactory connectionFactory,
    ProvisioningOptions options) : IProjectDatabaseCoordinator
{
    public PgConnectionSettings ConnectionSettings
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            return new PgConnectionSettings(
                builder.Host ?? throw new InvalidOperationException("Provisioning connection has no host."),
                builder.Port,
                builder.Username ?? throw new InvalidOperationException("Provisioning connection has no username."),
                builder.Password ?? throw new InvalidOperationException("Provisioning connection has no password."));
        }
    }

    public async Task<IProjectDatabaseLock> TryAcquireAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var connection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);
        try
        {
            await using (var command = new NpgsqlCommand(
                             "SELECT pg_try_advisory_lock(hashtext(@key))",
                             connection))
            {
                command.Parameters.AddWithValue("key", databaseName);
                if (await command.ExecuteScalarAsync(cancellationToken) is true)
                {
                    return new DatabaseLock(connection, databaseName);
                }
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return UnacquiredLock.Instance;
    }

    public async Task TerminateOtherConnectionsAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()",
            connection);
        command.Parameters.AddWithValue("db", databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Pooled runtime connectors for this database are now stale. Drop every idle connector
        // in this process so the next request opens a fresh connection instead of failing once.
        NpgsqlConnection.ClearAllPools();
    }

    private sealed class DatabaseLock(NpgsqlConnection connection, string databaseName) : IProjectDatabaseLock
    {
        public bool Acquired => true;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtext(@key))",
                    connection);
                command.Parameters.AddWithValue("key", databaseName);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // The connection is closing anyway; an unlock failure is not actionable.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class UnacquiredLock : IProjectDatabaseLock
    {
        public static readonly UnacquiredLock Instance = new();

        public bool Acquired => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private UnacquiredLock()
        {
        }
    }
}
