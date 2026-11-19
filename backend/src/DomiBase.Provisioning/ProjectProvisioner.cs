using DomiBase.Application.Projects;
using DomiBase.Domain.Projects;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Provisioning;

/// <summary>
/// Idempotent PostgreSQL provisioning for a project:
/// <list type="number">
///   <item>Restricted owner/anon/authenticated roles plus a runtime login.</item>
///   <item>Independent database <c>domibase_&lt;slug&gt;</c> owned by the owner role.</item>
///   <item>Database/schema permissions (no PUBLIC access, no PUBLIC CREATE).</item>
/// </list>
/// Each step checks current PostgreSQL state first, so retries are safe. Cleanup inspects the
/// real state and refuses to remove a database owned by a role that does not belong to the
/// project. A session-level advisory lock serializes provisioning and cleanup per database.
/// </summary>
public sealed class ProjectProvisioner(
    IProvisioningConnectionFactory connectionFactory,
    ILogger<ProjectProvisioner> logger) : IProjectProvisioner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private const int MaxStepErrorLength = 2000;

    public async Task<ProvisioningOutcome> ProvisionAsync(
        Project project,
        ProjectRuntimeCredentials runtimeCredentials,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null)
    {
        var steps = new List<ProvisioningStepResult>();

        if (!ProjectResourceNaming.TryResolve(project, out var names, out var namingError))
        {
            return new ProvisioningOutcome(false, namingError, steps);
        }

        if (!string.Equals(runtimeCredentials.Username, names.RuntimeRole, StringComparison.Ordinal))
        {
            return new ProvisioningOutcome(
                false,
                "Runtime credentials do not match the project role naming.",
                steps);
        }

        if (string.IsNullOrWhiteSpace(runtimeCredentials.Password))
        {
            return new ProvisioningOutcome(false, "Runtime credentials are missing a password.", steps);
        }

        try
        {
            await using var adminConnection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);

            if (!await TryAdvisoryLockAsync(adminConnection, names.DatabaseName, cancellationToken))
            {
                return new ProvisioningOutcome(
                    false,
                    "Another provisioning run is already in progress for this project.",
                    steps);
            }

            try
            {
                await RunStepAsync(
                    ProvisioningSteps.Roles,
                    async () =>
                    {
                        await EnsureRoleAsync(adminConnection, names.OwnerRole, cancellationToken);
                        await EnsureRoleAsync(adminConnection, names.AnonymousRole, cancellationToken);
                        await EnsureRoleAsync(adminConnection, names.AuthenticatedRole, cancellationToken);
                        await EnsureRuntimeLoginAsync(
                            adminConnection,
                            names.RuntimeRole,
                            runtimeCredentials.Password,
                            cancellationToken);

                        // NOINHERIT prevents ambient privilege inheritance; SET ROLE is explicit.
                        await ExecuteAsync(
                            adminConnection,
                            $"GRANT {Quote(names.AnonymousRole)} TO {Quote(names.RuntimeRole)}",
                            cancellationToken);
                        await ExecuteAsync(
                            adminConnection,
                            $"GRANT {Quote(names.AuthenticatedRole)} TO {Quote(names.RuntimeRole)}",
                            cancellationToken);
                    },
                    steps,
                    sink,
                    cancellationToken);

                await RunStepAsync(
                    ProvisioningSteps.Database,
                    () => EnsureDatabaseAsync(adminConnection, names.DatabaseName, names.OwnerRole, cancellationToken),
                    steps,
                    sink,
                    cancellationToken);

                await RunStepAsync(
                    ProvisioningSteps.DatabasePermissions,
                    () => ApplyDatabasePermissionsAsync(
                        names.DatabaseName,
                        names.RuntimeRole,
                        names.AnonymousRole,
                        names.AuthenticatedRole,
                        cancellationToken),
                    steps,
                    sink,
                    cancellationToken);

                await RunStepAsync(
                    ProvisioningSteps.AuthSchema,
                    () => ApplyAuthSchemaAsync(
                        names.DatabaseName,
                        names.OwnerRole,
                        names.RuntimeRole,
                        cancellationToken),
                    steps,
                    sink,
                    cancellationToken);

                logger.LogInformation(
                    "Provisioned project {Slug} (database {DatabaseName}).",
                    project.Slug,
                    names.DatabaseName);

                return new ProvisioningOutcome(true, null, steps);
            }
            finally
            {
                await TryAdvisoryUnlockAsync(adminConnection, names.DatabaseName);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any unexpected failure must end in a terminal state so the project is retryable.
            logger.LogError(exception, "Provisioning failed for project {Slug}.", project.Slug);
            var error = Describe(exception);
            if (steps.All(step => step.Succeeded))
            {
                steps.Add(new ProvisioningStepResult("provisioning", false, error));
            }

            return new ProvisioningOutcome(false, error, steps);
        }
    }

    public async Task EnsureAuthSchemaAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (!ProjectResourceNaming.TryResolve(project, out var names, out var error))
        {
            throw new InvalidOperationException(error);
        }

        await using var adminConnection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);

        await EnsureRoleAsync(adminConnection, names.AuthenticatedRole, cancellationToken);
        await ExecuteAsync(
            adminConnection,
            $"GRANT {Quote(names.AuthenticatedRole)} TO {Quote(names.RuntimeRole)}",
            cancellationToken);
        await ExecuteAsync(
            adminConnection,
            $"GRANT CONNECT ON DATABASE {Quote(names.DatabaseName)} TO {Quote(names.AuthenticatedRole)}",
            cancellationToken);

        await ApplyAuthSchemaAsync(names.DatabaseName, names.OwnerRole, names.RuntimeRole, cancellationToken);
    }

    public Task<DeprovisionOutcome> DeprovisionAsync(
        Project project,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null) =>
        RemoveResourcesAsync(project, cancellationToken, sink);

    public Task<DeprovisionOutcome> CompensateAsync(
        Project project,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null) =>
        RemoveResourcesAsync(project, cancellationToken, sink);

    private async Task<DeprovisionOutcome> RemoveResourcesAsync(
        Project project,
        CancellationToken cancellationToken,
        IProvisioningStepSink? sink)
    {
        var steps = new List<ProvisioningStepResult>();

        if (!ProjectResourceNaming.TryResolve(project, out var names, out var namingError))
        {
            logger.LogWarning("Refusing cleanup with invalid project metadata: {Reason}", namingError);
            return new DeprovisionOutcome(false, namingError, steps);
        }

        try
        {
            await using var adminConnection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);

            if (!await TryAdvisoryLockAsync(adminConnection, names.DatabaseName, cancellationToken))
            {
                return new DeprovisionOutcome(
                    false,
                    "Another provisioning or cleanup run is already in progress for this project.",
                    steps);
            }

            try
            {
                await RunStepAsync(
                    ProvisioningSteps.DatabaseCleanup,
                    () => DropDatabaseIfOwnedAsync(adminConnection, names, cancellationToken),
                    steps,
                    sink,
                    cancellationToken,
                    ProvisioningStatus.Compensated);

                await RunStepAsync(
                    ProvisioningSteps.RoleCleanup,
                    () => DropProjectRolesAsync(adminConnection, names, cancellationToken),
                    steps,
                    sink,
                    cancellationToken,
                    ProvisioningStatus.Compensated);

                logger.LogInformation(
                    "Removed PostgreSQL resources for project {Slug}.",
                    project.Slug);

                return new DeprovisionOutcome(true, null, steps);
            }
            finally
            {
                await TryAdvisoryUnlockAsync(adminConnection, names.DatabaseName);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Cleanup failed for project {Slug}.", project.Slug);
            var error = Describe(exception);
            if (steps.All(step => step.Succeeded))
            {
                steps.Add(new ProvisioningStepResult("cleanup", false, error));
            }

            return new DeprovisionOutcome(false, error, steps);
        }
    }

    private async Task RunStepAsync(
        string step,
        Func<Task> action,
        ICollection<ProvisioningStepResult> steps,
        IProvisioningStepSink? sink,
        CancellationToken cancellationToken,
        ProvisioningStatus successStatus = ProvisioningStatus.Succeeded)
    {
        Guid? operationId = null;

        try
        {
            if (sink is not null)
            {
                operationId = await sink.BeginAsync(step, cancellationToken);
            }

            await action();
            steps.Add(new ProvisioningStepResult(step, true, null));

            if (operationId is not null)
            {
                await sink!.CompleteAsync(operationId.Value, successStatus, null, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            var error = Truncate(Describe(exception), MaxStepErrorLength);
            steps.Add(new ProvisioningStepResult(step, false, error));

            if (operationId is not null)
            {
                try
                {
                    await sink!.CompleteAsync(
                        operationId.Value,
                        ProvisioningStatus.Failed,
                        error,
                        CancellationToken.None);
                }
                catch (Exception sinkException)
                {
                    // The original PostgreSQL failure is the actionable one.
                    logger.LogWarning(
                        sinkException,
                        "Could not persist the failed state of step {Step}.",
                        step);
                }
            }

            throw;
        }
    }

    private async Task DropDatabaseIfOwnedAsync(
        NpgsqlConnection connection,
        ProjectResourceNames names,
        CancellationToken cancellationToken)
    {
        var owner = await GetDatabaseOwnerAsync(connection, names.DatabaseName, cancellationToken);
        if (owner is null)
        {
            return;
        }

        if (!string.Equals(owner, names.OwnerRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{names.DatabaseName}' is owned by '{owner}', not by the project owner role. " +
                "Refusing to drop it.");
        }

        await using var terminate = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @database AND pid <> pg_backend_pid()
            """,
            connection)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };
        terminate.Parameters.AddWithValue("database", names.DatabaseName);
        await terminate.ExecuteNonQueryAsync(cancellationToken);

        await ExecuteAsync(connection, $"DROP DATABASE {Quote(names.DatabaseName)}", cancellationToken);
    }

    private async Task DropProjectRolesAsync(
        NpgsqlConnection connection,
        ProjectResourceNames names,
        CancellationToken cancellationToken)
    {
        var roles = new[]
        {
            names.AuthenticatedRole,
            names.AnonymousRole,
            names.RuntimeRole,
            names.OwnerRole
        };

        foreach (var role in roles)
        {
            if (await RoleExistsAsync(connection, role, cancellationToken))
            {
                await ExecuteAsync(connection, $"DROP ROLE {Quote(role)}", cancellationToken);
            }
        }
    }

    private async Task EnsureRoleAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        if (await RoleExistsAsync(connection, role, cancellationToken))
        {
            return;
        }

        await ExecuteAsync(
            connection,
            $"CREATE ROLE {Quote(role)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS",
            cancellationToken);
    }

    private async Task EnsureRuntimeLoginAsync(
        NpgsqlConnection connection,
        string role,
        string password,
        CancellationToken cancellationToken)
    {
        if (!await RoleExistsAsync(connection, role, cancellationToken))
        {
            await ExecuteAsync(
                connection,
                $"CREATE ROLE {Quote(role)} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS",
                cancellationToken);
        }

        // Idempotent: retries and key rotations converge on the stored password.
        // format(%I, %L) quotes the identifier and password literal safely.
        string alterSql;
        await using (var format = new NpgsqlCommand(
                         "SELECT format('ALTER ROLE %I LOGIN NOINHERIT PASSWORD %L', @role, @password)",
                         connection))
        {
            format.Parameters.AddWithValue("role", role);
            format.Parameters.AddWithValue("password", password);
            alterSql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        }

        await ExecuteAsync(connection, alterSql, cancellationToken);
    }

    private async Task EnsureDatabaseAsync(
        NpgsqlConnection connection,
        string databaseName,
        string ownerRole,
        CancellationToken cancellationToken)
    {
        var existingOwner = await GetDatabaseOwnerAsync(connection, databaseName, cancellationToken);

        if (existingOwner is not null)
        {
            if (!string.Equals(existingOwner, ownerRole, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database '{databaseName}' already exists but is owned by '{existingOwner}'. Refusing to modify it.");
            }

            return;
        }

        // CREATE DATABASE cannot run inside a transaction block.
        await ExecuteAsync(
            connection,
            $"CREATE DATABASE {Quote(databaseName)} OWNER {Quote(ownerRole)} ENCODING 'UTF8' TEMPLATE template0",
            cancellationToken);
    }

    private async Task ApplyDatabasePermissionsAsync(
        string databaseName,
        string runtimeRole,
        string anonymousRole,
        string authenticatedRole,
        CancellationToken cancellationToken)
    {
        await using var adminConnection = await connectionFactory.OpenAsync(cancellationToken: cancellationToken);

        await ExecuteAsync(
            adminConnection,
            $"REVOKE ALL ON DATABASE {Quote(databaseName)} FROM PUBLIC",
            cancellationToken);

        await ExecuteAsync(
            adminConnection,
            $"GRANT CONNECT ON DATABASE {Quote(databaseName)} TO {Quote(runtimeRole)}, {Quote(anonymousRole)}, {Quote(authenticatedRole)}",
            cancellationToken);

        await using var projectConnection = await connectionFactory.OpenAsync(databaseName, cancellationToken);

        // PostgreSQL 15+ already revokes CREATE on public from PUBLIC; make it explicit and idempotent.
        await ExecuteAsync(projectConnection, "REVOKE CREATE ON SCHEMA public FROM PUBLIC", cancellationToken);

        await ExecuteAsync(
            projectConnection,
            $"GRANT USAGE ON SCHEMA public TO {Quote(runtimeRole)}, {Quote(anonymousRole)}, {Quote(authenticatedRole)}",
            cancellationToken);
    }

    private async Task ApplyAuthSchemaAsync(
        string databaseName,
        string ownerRole,
        string runtimeRole,
        CancellationToken cancellationToken)
    {
        await using var projectConnection = await connectionFactory.OpenAsync(databaseName, cancellationToken);

        // Objects belong to the owner role; the runtime login gets DML only, never ownership.
        await ExecuteAsync(projectConnection, $"SET ROLE {Quote(ownerRole)}", cancellationToken);

        const string schemaSql = """
            CREATE SCHEMA IF NOT EXISTS auth;
            CREATE TABLE IF NOT EXISTS auth.users (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                email text NOT NULL,
                password_hash text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                disabled_at timestamptz
            );
            CREATE UNIQUE INDEX IF NOT EXISTS users_email_lower_key ON auth.users (lower(email));
            CREATE TABLE IF NOT EXISTS auth.refresh_tokens (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
                family_id uuid NOT NULL,
                token_hash text NOT NULL UNIQUE,
                created_at timestamptz NOT NULL DEFAULT now(),
                expires_at timestamptz NOT NULL,
                consumed_at timestamptz,
                revoked_at timestamptz
            );
            CREATE INDEX IF NOT EXISTS refresh_tokens_user_idx ON auth.refresh_tokens (user_id);
            CREATE INDEX IF NOT EXISTS refresh_tokens_family_idx ON auth.refresh_tokens (family_id);
            """;

        await ExecuteAsync(projectConnection, schemaSql, cancellationToken);

        // Only the server-side runtime login touches the auth schema. End-user roles never
        // receive privileges here, and the Runtime data API only exposes public tables.
        await ExecuteAsync(
            projectConnection,
            $"GRANT USAGE ON SCHEMA auth TO {Quote(runtimeRole)}",
            cancellationToken);

        await ExecuteAsync(
            projectConnection,
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA auth TO {Quote(runtimeRole)}",
            cancellationToken);
    }

    private static async Task<bool> RoleExistsAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_roles WHERE rolname = @name",
            connection);
        command.Parameters.AddWithValue("name", role);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<string?> GetDatabaseOwnerAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name",
            connection);
        command.Parameters.AddWithValue("name", databaseName);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<bool> TryAdvisoryLockAsync(
        NpgsqlConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtext(@key))",
            connection);
        command.Parameters.AddWithValue("key", key);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task TryAdvisoryUnlockAsync(NpgsqlConnection connection, string key)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext(@key))",
                connection);
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (NpgsqlException exception)
        {
            // Connection is being closed anyway; unlocking failure is not actionable.
            _ = exception;
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)CommandTimeout.TotalSeconds
        };

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Describe(Exception exception) => exception switch
    {
        PostgresException postgres => $"PostgreSQL {postgres.SqlState}: {postgres.MessageText}",
        _ => $"{exception.GetType().Name}: {exception.Message}"
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
