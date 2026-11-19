using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

public class ProjectDeletionTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task DeleteProject_WithValidConfirmation_RemovesResourcesAndKeepsAudit()
    {
        const string deletedSlug = "it-delete-target";
        const string survivorSlug = "it-delete-survivor";
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        Guid deletedId = Guid.Empty;
        Guid survivorId = Guid.Empty;

        try
        {
            using var client = factory.CreateAdminClient();
            deletedId = await CreateProjectAsync(client, deletedSlug);
            survivorId = await CreateProjectAsync(client, survivorSlug);

            var keyResponse = await client.PostAsJsonAsync(
                $"/api/v1/projects/{deletedId}/keys",
                new { label = "delete-test" });
            Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);

            using var response = await SendDeleteAsync(client, deletedId, deletedSlug);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            var deletedArtifacts = await ReadArtifactsAsync(deletedSlug);
            Assert.False(deletedArtifacts.Database);
            Assert.False(deletedArtifacts.Owner);
            Assert.False(deletedArtifacts.Runtime);

            Assert.True((await ReadArtifactsAsync(survivorSlug)).Database);
            Assert.Equal(0, await CountControlRowsAsync(
                "SELECT COUNT(*) FROM projects WHERE id = @id", deletedId));
            Assert.Equal(0, await CountControlRowsAsync(
                "SELECT COUNT(*) FROM api_keys WHERE project_id = @id", deletedId));
            Assert.Equal(0, await CountControlRowsAsync(
                "SELECT COUNT(*) FROM provisioning_operations WHERE project_id = @id", deletedId));
            Assert.Equal(1, await CountControlRowsAsync(
                "SELECT COUNT(*) FROM audit_entries WHERE target_id = @id::text AND action = 'project.deleted'", deletedId));

            using var repeated = await SendDeleteAsync(client, deletedId, deletedSlug);
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
            Assert.True((await ReadArtifactsAsync(survivorSlug)).Database);
        }
        finally
        {
            await cleanup.DropProjectAsync(deletedSlug, deletedId == Guid.Empty ? null : deletedId);
            await cleanup.DropProjectAsync(survivorSlug, survivorId == Guid.Empty ? null : survivorId);
        }
    }

    [Fact]
    public async Task DeleteProject_WithoutValidConfirmation_IsRejectedAndKeepsProject()
    {
        var slug = UniqueSlug();
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        Guid projectId = Guid.Empty;

        try
        {
            using var client = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(client, slug);

            using var missingHeader = await SendDeleteAsync(client, projectId, slug, confirmation: null);
            Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);

            using var wrongSlug = await SendDeleteAsync(client, projectId, "otro-proyecto");
            Assert.Equal(HttpStatusCode.BadRequest, wrongSlug.StatusCode);

            using var missingSlug = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/projects/{projectId}")
            {
                Content = JsonContent.Create(new { })
            };
            missingSlug.Headers.Add("X-DomiBase-Confirm-Delete", "delete");
            using var missingSlugResponse = await client.SendAsync(missingSlug);
            Assert.Equal(HttpStatusCode.BadRequest, missingSlugResponse.StatusCode);

            Assert.Equal(1, await CountControlRowsAsync(
                "SELECT COUNT(*) FROM projects WHERE id = @id", projectId));
            Assert.True((await ReadArtifactsAsync(slug)).Database);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = "Deletion Test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync(
        HttpClient client,
        Guid projectId,
        string confirmSlug,
        string? confirmation = "delete")
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/projects/{projectId}")
        {
            Content = JsonContent.Create(new { confirmSlug })
        };

        if (confirmation is not null)
        {
            request.Headers.Add("X-DomiBase-Confirm-Delete", confirmation);
        }

        return await client.SendAsync(request);
    }

    private async Task<int> CountControlRowsAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(bool Database, bool Owner, bool Runtime)> ReadArtifactsAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(factory.ProvisioningConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @database),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @owner),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @runtime)
            """,
            connection);
        command.Parameters.AddWithValue("database", ProjectNaming.ToDatabaseName(slug));
        command.Parameters.AddWithValue("owner", ProjectNaming.ToOwnerRole(slug));
        command.Parameters.AddWithValue("runtime", ProjectNaming.ToRuntimeRole(slug));

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private static string UniqueSlug() => "del-" + Guid.NewGuid().ToString("N")[..10];
}
