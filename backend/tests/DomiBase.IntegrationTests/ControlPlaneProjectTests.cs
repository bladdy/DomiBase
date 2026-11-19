using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

public class ControlPlaneProjectTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task ProjectAndKeyLifecycle_WorksAgainstRealPostgreSql()
    {
        var slug = UniqueSlug();
        Guid? projectId = null;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var client = factory.CreateAdminClient();

            var createResponse = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = "Integration Test" });
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

            using var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
            var project = createJson.RootElement;
            projectId = project.GetProperty("id").GetGuid();

            Assert.Equal(slug, project.GetProperty("slug").GetString());
            Assert.Equal("Ready", project.GetProperty("status").GetString());
            Assert.Equal(ProjectNaming.ToDatabaseName(slug), project.GetProperty("databaseName").GetString());

            var artifacts = await ReadDatabaseArtifactsAsync(slug);
            Assert.True(artifacts.DatabaseExists, "Project database was not created.");
            Assert.True(artifacts.OwnerRoleExists, "Owner role was not created.");
            Assert.True(artifacts.RuntimeRoleExists, "Runtime role was not created.");
            Assert.True(artifacts.AnonymousRoleExists, "Anonymous role was not created.");

            var getResponse = await client.GetAsync($"/api/v1/projects/{projectId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

            var listResponse = await client.GetAsync("/api/v1/projects");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            Assert.Contains(slug, await listResponse.Content.ReadAsStringAsync());

            var duplicateResponse = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = "Duplicate" });
            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

            var secretKeyResponse = await client.PostAsJsonAsync(
                $"/api/v1/projects/{projectId}/keys",
                new { kind = "secret" });
            Assert.Equal(HttpStatusCode.Created, secretKeyResponse.StatusCode);
            using var secretJson = JsonDocument.Parse(await secretKeyResponse.Content.ReadAsStringAsync());
            Assert.Equal("Secret", secretJson.RootElement.GetProperty("kind").GetString());
            Assert.StartsWith("db_secret_", secretJson.RootElement.GetProperty("key").GetString());

            var keyResponse = await client.PostAsJsonAsync(
                $"/api/v1/projects/{projectId}/keys",
                new { label = "integration" });
            Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);

            using var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            var keyId = keyJson.RootElement.GetProperty("id").GetGuid();
            var fullKey = keyJson.RootElement.GetProperty("key").GetString();

            Assert.NotNull(fullKey);
            Assert.StartsWith("db_public_", fullKey);
            Assert.True(fullKey!.Length > 30);
            Assert.StartsWith("db_public_", keyJson.RootElement.GetProperty("prefix").GetString());

            var storedHash = await ReadKeyHashAsync(keyId);
            Assert.NotNull(storedHash);
            Assert.NotEqual(fullKey, storedHash);
            Assert.Equal(64, storedHash!.Length);

            var keysResponse = await client.GetAsync($"/api/v1/projects/{projectId}/keys");
            var keysBody = await keysResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, keysResponse.StatusCode);
            Assert.DoesNotContain(fullKey, keysBody);
            Assert.DoesNotContain("secretHash", keysBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(keyId.ToString(), keysBody);

            var firstRevoke = await client.DeleteAsync($"/api/v1/projects/{projectId}/keys/{keyId}");
            var secondRevoke = await client.DeleteAsync($"/api/v1/projects/{projectId}/keys/{keyId}");
            Assert.Equal(HttpStatusCode.NoContent, firstRevoke.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, secondRevoke.StatusCode);

            var retryResponse = await client.PostAsync($"/api/v1/projects/{projectId}/provision", null);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            using var retryJson = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
            Assert.Equal("Ready", retryJson.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId);
        }
    }

    [Theory]
    [InlineData("1invalid", "Name")]
    [InlineData("", "Name")]
    [InlineData("valid-slug", "")]
    public async Task CreateProject_WithInvalidInput_ReturnsValidationError(string slug, string name)
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task CreateProject_WithMissingBody_ReturnsValidationError()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/v1/projects", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task UnknownProject_ReturnsNotFound()
    {
        using var client = factory.CreateAdminClient();
        var unknownId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{unknownId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{unknownId}/keys")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/v1/projects/{unknownId}/keys", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/v1/projects/{unknownId}/provision", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/v1/projects/{unknownId}/keys/{Guid.NewGuid()}")).StatusCode);
    }

    private async Task<(bool DatabaseExists, bool OwnerRoleExists, bool RuntimeRoleExists, bool AnonymousRoleExists)>
        ReadDatabaseArtifactsAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(factory.ProvisioningConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @database),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @owner),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @runtime),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @anon)
            """,
            connection);

        command.Parameters.AddWithValue("database", ProjectNaming.ToDatabaseName(slug));
        command.Parameters.AddWithValue("owner", ProjectNaming.ToOwnerRole(slug));
        command.Parameters.AddWithValue("runtime", ProjectNaming.ToRuntimeRole(slug));
        command.Parameters.AddWithValue("anon", ProjectNaming.ToAnonymousRole(slug));

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    private async Task<string?> ReadKeyHashAsync(Guid keyId)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT secret_hash FROM api_keys WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", keyId);

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static string UniqueSlug() => "it-" + Guid.NewGuid().ToString("N")[..10];
}
