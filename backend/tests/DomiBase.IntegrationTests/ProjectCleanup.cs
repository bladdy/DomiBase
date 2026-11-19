using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Removes artifacts created by integration tests: the project database,
/// its three project roles and the metadata rows. Never touches other projects.
/// </summary>
public sealed class ProjectCleanup(string provisioningConnectionString, string controlConnectionString)
{
    public async Task DropProjectAsync(string slug, Guid? projectId)
    {
        var databaseName = ProjectNaming.ToDatabaseName(slug);
        var roles = new[]
        {
            ProjectNaming.ToOwnerRole(slug),
            ProjectNaming.ToAuthenticatedRole(slug),
            ProjectNaming.ToRuntimeRole(slug),
            ProjectNaming.ToAnonymousRole(slug)
        };

        await using (var admin = new NpgsqlConnection(provisioningConnectionString))
        {
            await admin.OpenAsync();

            await using (var terminate = new NpgsqlCommand(
                             "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()",
                             admin))
            {
                terminate.Parameters.AddWithValue("db", databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {Quote(databaseName)}");

            foreach (var role in roles)
            {
                await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {Quote(role)}");
            }
        }

        if (projectId is null)
        {
            return;
        }

        await using var control = new NpgsqlConnection(controlConnectionString);
        await control.OpenAsync();

        await using (var audit = new NpgsqlCommand(
                         """
                         DELETE FROM audit_entries
                         WHERE target_id = @projectId
                            OR target_id IN (SELECT id::text FROM api_keys WHERE project_id = @projectIdGuid)
                         """,
                         control))
        {
            audit.Parameters.AddWithValue("projectId", projectId.Value.ToString());
            audit.Parameters.AddWithValue("projectIdGuid", projectId.Value);
            await audit.ExecuteNonQueryAsync();
        }

        await using (var project = new NpgsqlCommand("DELETE FROM projects WHERE id = @id", control))
        {
            project.Parameters.AddWithValue("id", projectId.Value);
            await project.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
