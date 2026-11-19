using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Real PostgreSQL coverage for the allowlisted extension catalog. Every test provisions its
/// own project and verifies live catalog state, DDL idempotency, owner-role execution,
/// project isolation and non-secret audit entries.
/// </summary>
public sealed class ExtensionTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    private const string Extension = "pgcrypto";

    [Fact]
    public async Task List_ReturnsAllowlistedExtensionsWithLiveState()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var response = await admin.GetAsync($"/api/v1/projects/{project.Id}/extensions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extensions = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(10, extensions.Length);

        var pgcrypto = extensions.Single(item => item.GetProperty("name").GetString() == Extension);
        Assert.True(pgcrypto.GetProperty("trusted").GetBoolean());
        Assert.True(pgcrypto.GetProperty("available").GetBoolean());
        Assert.False(pgcrypto.GetProperty("installed").GetBoolean());
        Assert.True(pgcrypto.GetProperty("canInstall").GetBoolean());
        Assert.False(pgcrypto.GetProperty("canUninstall").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(pgcrypto.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task Install_IsIdempotentAndVisibleInCatalog()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var first = await InstallAsync(admin, project.Id, Extension);
        var second = await InstallAsync(admin, project.Id, Extension);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountInstalledAsync(project.DatabaseName, Extension));

        var listed = await GetExtensionAsync(admin, project.Id, Extension);
        Assert.True(listed.GetProperty("installed").GetBoolean());
        Assert.False(listed.GetProperty("canInstall").GetBoolean());
        Assert.True(listed.GetProperty("canUninstall").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("installedVersion").GetString()));
    }

    [Fact]
    public async Task Uninstall_IsIdempotentAndRemovesExtension()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(admin, project.Id, Extension)).StatusCode);

        var first = await UninstallAsync(admin, project.Id, Extension);
        var second = await UninstallAsync(admin, project.Id, Extension);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, await CountInstalledAsync(project.DatabaseName, Extension));

        var listed = await GetExtensionAsync(admin, project.Id, Extension);
        Assert.False(listed.GetProperty("installed").GetBoolean());
        Assert.True(listed.GetProperty("canInstall").GetBoolean());
    }

    [Fact]
    public async Task Install_UsesTheProjectOwnerRole()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(admin, project.Id, Extension)).StatusCode);

        await using var connection = await OpenProjectAsync(project.DatabaseName);
        await using var command = new NpgsqlCommand(
            "SELECT extowner::regrole::text FROM pg_catalog.pg_extension WHERE extname = @name",
            connection);
        command.Parameters.AddWithValue("name", Extension);
        var owner = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal(project.OwnerRole, owner);
    }

    [Theory]
    [InlineData("dblink")]
    [InlineData("plpython3u")]
    [InlineData("pgcrypto\"; DROP EXTENSION \"pgcrypto")]
    public async Task NamesOutsideAllowlist_AreRejectedWithoutTouchingTheDatabase(string name)
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var install = await InstallAsync(admin, project.Id, name);
        var uninstall = await UninstallAsync(admin, project.Id, name);

        Assert.Equal(HttpStatusCode.BadRequest, install.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(install));
        Assert.Equal(HttpStatusCode.BadRequest, uninstall.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(uninstall));
        Assert.Equal(0, await CountInstalledAsync(project.DatabaseName, Extension));

        var body = await install.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DROP EXTENSION", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingName_IsRejected()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{project.Id}/extensions",
            new { name = string.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task UnknownProject_ReturnsNotFound()
    {
        using var admin = factory.CreateAdminClient();
        var id = Guid.NewGuid();

        var list = await admin.GetAsync($"/api/v1/projects/{id}/extensions");
        var install = await InstallAsync(admin, id, Extension);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal("NOT_FOUND", await ReadErrorCodeAsync(list));
        Assert.Equal(HttpStatusCode.NotFound, install.StatusCode);
        Assert.Equal("NOT_FOUND", await ReadErrorCodeAsync(install));
    }

    [Fact]
    public async Task Installation_IsScopedToTheRequestedProject()
    {
        await using var own = await CreateProjectAsync();
        await using var other = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(admin, own.Id, Extension)).StatusCode);

        var otherExtension = await GetExtensionAsync(admin, other.Id, Extension);
        Assert.False(otherExtension.GetProperty("installed").GetBoolean());
        Assert.Equal(0, await CountInstalledAsync(other.DatabaseName, Extension));
    }

    [Fact]
    public async Task Operations_AreAuditedWithoutSqlOrSecrets()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/projects/{project.Id}/extensions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(admin, project.Id, Extension)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UninstallAsync(admin, project.Id, Extension)).StatusCode);

        await using var control = new NpgsqlConnection(factory.ControlConnectionString);
        await control.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(action || ':' || details::text, '|')
            FROM audit_entries
            WHERE target_id = @id AND action LIKE 'extension.%'
            """,
            control);
        command.Parameters.AddWithValue("id", project.Id.ToString());
        var audit = (string)(await command.ExecuteScalarAsync())!;

        Assert.Contains("extension.list", audit, StringComparison.Ordinal);
        Assert.Contains("extension.install", audit, StringComparison.Ordinal);
        Assert.Contains("extension.uninstall", audit, StringComparison.Ordinal);
        Assert.Contains(project.DatabaseName, audit, StringComparison.Ordinal);
        Assert.Contains(Extension, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE EXTENSION", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP EXTENSION", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", audit, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TestProject> CreateProjectAsync()
    {
        var slug = "ext-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Extension catalog test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetGuid();
        return new TestProject(factory, id, slug);
    }

    private async Task<NpgsqlConnection> OpenProjectAsync(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ProvisioningConnectionString)
        {
            Database = database
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<int> CountInstalledAsync(string database, string name)
    {
        await using var connection = await OpenProjectAsync(database);
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM pg_catalog.pg_extension WHERE extname = @name",
            connection);
        command.Parameters.AddWithValue("name", name);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static Task<HttpResponseMessage> InstallAsync(HttpClient admin, Guid projectId, string name) =>
        admin.PostAsJsonAsync($"/api/v1/projects/{projectId}/extensions", new { name });

    private static Task<HttpResponseMessage> UninstallAsync(HttpClient admin, Guid projectId, string name) =>
        admin.DeleteAsync($"/api/v1/projects/{projectId}/extensions/{Uri.EscapeDataString(name)}");

    private static async Task<JsonElement> GetExtensionAsync(HttpClient admin, Guid projectId, string name)
    {
        var response = await admin.GetAsync($"/api/v1/projects/{projectId}/extensions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name)
            .Clone();
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private sealed class TestProject(
        DomiBaseWebApplicationFactory factory,
        Guid id,
        string slug) : IAsyncDisposable
    {
        public Guid Id { get; } = id;

        public string DatabaseName { get; } = ProjectNaming.ToDatabaseName(slug);

        public string OwnerRole { get; } = ProjectNaming.ToOwnerRole(slug);

        public ValueTask DisposeAsync() => new(
            new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString)
                .DropProjectAsync(slug, Id));
    }
}
