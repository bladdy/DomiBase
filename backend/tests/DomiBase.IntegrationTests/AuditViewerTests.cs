using System.Net;
using System.Text.Json;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Real PostgreSQL coverage for the read-only audit viewer: project-scoped filtering,
/// bounded pagination, validation errors and redaction of legacy secret-shaped diagnostics.
/// </summary>
public class AuditViewerTests
{
    private static readonly Guid MissingProjectId = Guid.Parse("99999999-9999-7999-8999-999999999999");

    [Fact]
    public async Task AuditEndpoint_ListsProjectEventsWithFiltersAndPagination()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(admin, "audit-filter");

        try
        {
            using var createdKey = await admin.PostAsync(
                $"/api/v1/projects/{project.Id}/keys",
                new StringContent("{\"kind\":\"Public\"}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, createdKey.StatusCode);

            using var scoped = await admin.GetAsync($"/api/v1/audit?projectId={project.Id}&action=api_key.create");
            Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
            using var scopedJson = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync());
            Assert.True(scopedJson.RootElement.GetProperty("total").GetInt32() >= 1);
            Assert.All(
                scopedJson.RootElement.GetProperty("items").EnumerateArray(),
                item =>
                {
                    Assert.Equal("api_key.create", item.GetProperty("action").GetString());
                    Assert.Equal(project.Id, item.GetProperty("projectId").GetGuid());
                });

            using var empty = await admin.GetAsync($"/api/v1/audit?projectId={project.Id}&action=does.not.exist");
            using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal(0, emptyJson.RootElement.GetProperty("total").GetInt32());

            using var paged = await admin.GetAsync($"/api/v1/audit?projectId={project.Id}&limit=1&offset=1");
            using var pagedJson = JsonDocument.Parse(await paged.Content.ReadAsStringAsync());
            Assert.Equal(1, pagedJson.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(1, pagedJson.RootElement.GetProperty("offset").GetInt32());
            Assert.True(pagedJson.RootElement.GetProperty("total").GetInt32() >= 2);
            Assert.Single(pagedJson.RootElement.GetProperty("items").EnumerateArray());

            using var isolated = await admin.GetAsync($"/api/v1/audit?projectId={MissingProjectId}");
            using var isolatedJson = JsonDocument.Parse(await isolated.Content.ReadAsStringAsync());
            Assert.Equal(0, isolatedJson.RootElement.GetProperty("total").GetInt32());
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    [Theory]
    [InlineData("/api/v1/audit?unknown=1")]
    [InlineData("/api/v1/audit?projectId=not-a-guid")]
    [InlineData("/api/v1/audit?action=project.create%27%20OR%201%3D1")]
    [InlineData("/api/v1/audit?limit=201")]
    [InlineData("/api/v1/audit?offset=-1")]
    [InlineData("/api/v1/audit?from=2026-11-19T00:00:00Z&to=2026-11-18T00:00:00Z")]
    public async Task AuditEndpoint_RejectsInvalidFilters(string path)
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();

        using var response = await admin.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuditViewer_RedactsLegacySecretShapesFromControlDatabase()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(admin, "audit-redact");
        var legacyEntryId = Guid.NewGuid();
        var legacyOperationId = Guid.NewGuid();

        try
        {
            await SeedLegacyRowsAsync(factory, project.Id, legacyEntryId, legacyOperationId);

            using var audit = await admin.GetAsync($"/api/v1/audit?action=test.legacy.secret");
            using var auditJson = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
            var details = auditJson.RootElement.GetProperty("items")[0].GetProperty("details").GetString()!;
            Assert.Contains("[redacted]", details);
            Assert.DoesNotContain("db_public_legacysecretvalue", details);
            Assert.DoesNotContain("legacy-password", details);

            using var operations = await admin.GetAsync($"/api/v1/projects/{project.Id}/provisioning-operations");
            Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
            using var list = JsonDocument.Parse(await operations.Content.ReadAsStringAsync());
            // Provisioning operations are returned as a bare list.
            var legacyError = list.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == legacyOperationId)
                .GetProperty("error").GetString()!;
            Assert.Contains("[redacted]", legacyError);
            Assert.DoesNotContain("legacy-password", legacyError);
        }
        finally
        {
            await DeleteSeededRowsAsync(factory, legacyEntryId, legacyOperationId);
            await CleanupAsync(factory, project);
        }
    }

    [Fact]
    public async Task ProvisioningEndpoint_ListsRealStepsAndRejectsUnknownProject()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(admin, "audit-steps");

        try
        {
            using var response = await admin.GetAsync($"/api/v1/projects/{project.Id}/provisioning-operations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetArrayLength() >= 1);
            Assert.Contains(
                json.RootElement.EnumerateArray(),
                item => item.GetProperty("status").GetString() == "Succeeded");

            using var missing = await admin.GetAsync($"/api/v1/projects/{MissingProjectId}/provisioning-operations");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    private static async Task<ProjectInfo> CreateProjectAsync(HttpClient admin, string slug)
    {
        using var response = await admin.PostAsync("/api/v1/projects",
            new StringContent(
                JsonSerializer.Serialize(new { slug, name = slug }),
                System.Text.Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ProjectInfo(slug, json.RootElement.GetProperty("id").GetGuid());
    }

    private static async Task SeedLegacyRowsAsync(
        DomiBaseWebApplicationFactory factory,
        Guid projectId,
        Guid entryId,
        Guid operationId)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();

        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO audit_entries (id, occurred_at, actor, action, target_type, target_id, project_id, details)
            VALUES (@id, now(), 'admin', 'test.legacy.secret', 'project', @target, @projectId,
                    CAST(@details AS jsonb))
            """, connection))
        {
            audit.Parameters.AddWithValue("id", entryId);
            audit.Parameters.AddWithValue("target", projectId.ToString());
            audit.Parameters.AddWithValue("projectId", projectId);
            audit.Parameters.AddWithValue("details",
                "{\"apiKey\":\"db_public_legacysecretvalue123\",\"password\":\"legacy-password\"}");
            await audit.ExecuteNonQueryAsync();
        }

        await using (var operation = new NpgsqlCommand(
            """
            INSERT INTO provisioning_operations
                (id, project_id, step, status, attempt, error, started_at)
            VALUES (@id, @projectId, 'legacy_step', 'Failed', 1, @error, now())
            """, connection))
        {
            operation.Parameters.AddWithValue("id", operationId);
            operation.Parameters.AddWithValue("projectId", projectId);
            operation.Parameters.AddWithValue("error", "pg_restore failed: PGPASSWORD=legacy-password");
            await operation.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeleteSeededRowsAsync(
        DomiBaseWebApplicationFactory factory,
        Guid entryId,
        Guid operationId)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM audit_entries WHERE id = @entryId; DELETE FROM provisioning_operations WHERE id = @operationId",
            connection);
        command.Parameters.AddWithValue("entryId", entryId);
        command.Parameters.AddWithValue("operationId", operationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync(DomiBaseWebApplicationFactory factory, ProjectInfo project)
    {
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        await cleanup.DropProjectAsync(project.Slug, project.Id);
    }

    private sealed record ProjectInfo(string Slug, Guid Id);
}
