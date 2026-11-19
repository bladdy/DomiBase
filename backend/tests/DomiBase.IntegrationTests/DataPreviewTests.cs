using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Real PostgreSQL verification of the Control Plane row preview. The key assertion is that
/// the panel sees exactly what the anonymous Runtime role sees: RLS, grants and the project
/// context are never bypassed even though the caller is the administrator.
/// </summary>
[Collection(RuntimeCollection.Name)]
public sealed class DataPreviewTests(RuntimeProjectFixture fixture)
{
    [Fact]
    public async Task Preview_RunsAsAnonymousRoleAndAppliesSelectPolicy()
    {
        using var runtime = fixture.CreateRuntimeClientWithKey();
        using var admin = fixture.Factory.CreateAdminClient();
        var email = "preview-" + Guid.NewGuid().ToString("N") + "@example.test";

        var insert = await runtime.PostAsJsonAsync(
            "/rest/v1/contactos",
            new { nombre = "Vista previa", email });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        var response = await admin.GetAsync(
            $"/api/v1/projects/{fixture.ProjectId}/data/tables/contactos/rows" +
            $"?select=id,email,estado&f=email.eq.{Uri.EscapeDataString(email)}&limit=5");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("anon", root.GetProperty("role").GetString());
        Assert.Equal(1, root.GetProperty("rowCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("email", root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(email, root.GetProperty("rows")[0].GetProperty("email").GetString());
        Assert.Equal("nuevo", root.GetProperty("rows")[0].GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Preview_WithoutSelectPolicy_ReturnsNoRows()
    {
        await SeedValueAsync("public.privado", "secreto", "secreto-de-prueba");
        using var admin = fixture.Factory.CreateAdminClient();

        var response = await admin.GetAsync(
            $"/api/v1/projects/{fixture.ProjectId}/data/tables/privado/rows");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("anon", root.GetProperty("role").GetString());
        Assert.Equal(0, root.GetProperty("rowCount").GetInt32());
        Assert.Empty(root.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task Preview_WithProjectScopedPolicy_UsesServerSideProjectContext()
    {
        using var admin = fixture.Factory.CreateAdminClient();

        var response = await admin.GetAsync(
            $"/api/v1/projects/{fixture.ProjectId}/data/tables/tenant_items/rows" +
            "?select=project_id,nombre&f=nombre.eq.propio");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("rowCount").GetInt32());
        Assert.Equal("propio", root.GetProperty("rows")[0].GetProperty("nombre").GetString());
        Assert.DoesNotContain("ajeno", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_RejectsUnexposedTablesAndInjectedIdentifiers()
    {
        using var admin = fixture.Factory.CreateAdminClient();
        var basePath = $"/api/v1/projects/{fixture.ProjectId}/data/tables";

        var withoutRls = await admin.GetAsync($"{basePath}/sin_rls/rows");
        Assert.Equal(HttpStatusCode.NotFound, withoutRls.StatusCode);
        Assert.Equal("NOT_FOUND", await ReadErrorCodeAsync(withoutRls));

        var withoutGrants = await admin.GetAsync($"{basePath}/interno/rows");
        Assert.Equal(HttpStatusCode.NotFound, withoutGrants.StatusCode);
        Assert.Equal("NOT_FOUND", await ReadErrorCodeAsync(withoutGrants));

        var injectedTable = Uri.EscapeDataString("contactos;DROP TABLE privado");
        var injection = await admin.GetAsync($"{basePath}/{injectedTable}/rows");
        Assert.Equal(HttpStatusCode.BadRequest, injection.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(injection));

        var injectedColumn = await admin.GetAsync(
            $"{basePath}/contactos/rows?select={Uri.EscapeDataString("id,email);DROP TABLE privado")}");
        Assert.Equal(HttpStatusCode.BadRequest, injectedColumn.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(injectedColumn));

        // The decoy table must still exist after the rejected identifier attempts.
        Assert.True(await TableExistsAsync("public.privado"));
    }

    [Fact]
    public async Task Preview_IsIsolatedBetweenProjects()
    {
        var firstValue = "primero-" + Guid.NewGuid().ToString("N")[..8];
        var secondValue = "segundo-" + Guid.NewGuid().ToString("N")[..8];
        await SeedPreviewItemsAsync(fixture.Slug, fixture.ProjectId, firstValue);

        await using var second = await CreateProjectAsync();
        await SeedPreviewItemsAsync(second.Slug, second.Id, secondValue);

        using var admin = fixture.Factory.CreateAdminClient();
        var bodyA = await ReadBodyAsync(admin, fixture.ProjectId);
        var bodyB = await ReadBodyAsync(admin, second.Id);

        Assert.Contains(firstValue, bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, bodyA, StringComparison.Ordinal);
        Assert.Contains(secondValue, bodyB, StringComparison.Ordinal);
        Assert.DoesNotContain(firstValue, bodyB, StringComparison.Ordinal);
    }

    private static async Task<string> ReadBodyAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.GetAsync(
            $"/api/v1/projects/{projectId}/data/tables/preview_items/rows");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<TestProject> CreateProjectAsync()
    {
        var slug = "preview-" + Guid.NewGuid().ToString("N")[..10];
        using var admin = fixture.Factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Preview isolation test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new TestProject(fixture.Factory, json.RootElement.GetProperty("id").GetGuid(), slug);
    }

    private async Task SeedPreviewItemsAsync(string slug, Guid projectId, string value)
    {
        var database = ProjectNaming.ToDatabaseName(slug);
        var owner = ProjectNaming.ToOwnerRole(slug);
        var anon = ProjectNaming.ToAnonymousRole(slug);

        var builder = new NpgsqlConnectionStringBuilder(fixture.Factory.ProvisioningConnectionString)
        {
            Database = database
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE {Quote(owner)}");

        var ddl = $"""
            DROP TABLE IF EXISTS public.preview_items;
            CREATE TABLE public.preview_items (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                project_id uuid NOT NULL,
                valor text NOT NULL
            );
            ALTER TABLE public.preview_items ENABLE ROW LEVEL SECURITY;
            GRANT SELECT ON public.preview_items TO {Quote(anon)};
            CREATE POLICY preview_items_select ON public.preview_items FOR SELECT TO {Quote(anon)}
                USING (project_id = current_setting('domibase.project_id', true)::uuid);
            INSERT INTO public.preview_items (project_id, valor)
                VALUES ('{projectId}', '{value}');
            """;
        await ExecuteAsync(connection, ddl);
    }

    private async Task SeedValueAsync(string table, string column, string value)
    {
        await using var connection = await fixture.OpenProjectDatabaseAsync();
        await ExecuteAsync(connection, $"SET ROLE {Quote(fixture.OwnerRole)}");
        await ExecuteAsync(
            connection,
            $"INSERT INTO {table} ({column}) VALUES ('{value}')");
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = await fixture.OpenProjectDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@name) IS NOT NULL",
            connection);
        command.Parameters.AddWithValue("name", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private sealed class TestProject : IAsyncDisposable
    {
        private readonly DomiBaseWebApplicationFactory _factory;
        private readonly Guid _id;
        private readonly string _slug;

        public TestProject(DomiBaseWebApplicationFactory factory, Guid id, string slug)
        {
            _factory = factory;
            _id = id;
            _slug = slug;
            Id = id;
            Slug = slug;
        }

        public Guid Id { get; }

        public string Slug { get; }

        public ValueTask DisposeAsync() => new(
            new ProjectCleanup(_factory.ProvisioningConnectionString, _factory.ControlConnectionString)
                .DropProjectAsync(_slug, _id));
    }
}
