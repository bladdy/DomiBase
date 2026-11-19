using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Real PostgreSQL verification of the administrative SQL editor: JSON-shaped results, row
/// caps, read-only enforcement, timeout, safe errors and audit trail. Each test owns its project.
/// </summary>
public sealed class SqlQueryTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    private const string Table = "sql_items";

    [Fact]
    public async Task Query_ReturnsTypedColumnsAndJsonRows()
    {
        await using var project = await CreateProjectAsync();
        await SeedAsync(project);
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(admin, project.Id, "SELECT id, nombre FROM sql_items ORDER BY id");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("rowCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal("id", root.GetProperty("columns")[0].GetString());
        Assert.Equal("nombre", root.GetProperty("columns")[1].GetString());
        Assert.Equal(1, root.GetProperty("rows")[0].GetProperty("id").GetInt32());
        Assert.Equal("alfa", root.GetProperty("rows")[0].GetProperty("nombre").GetString());
        Assert.Equal(2, root.GetProperty("rows")[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Query_WithCteAndJsonValues_Works()
    {
        await using var project = await CreateProjectAsync();
        await SeedAsync(project);
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(
            admin,
            project.Id,
            "WITH datos AS (SELECT id, nombre FROM sql_items) SELECT id, nombre FROM datos WHERE id = 2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal("beta", json.RootElement.GetProperty("rows")[0].GetProperty("nombre").GetString());
    }

    [Fact]
    public async Task Query_RespectsRowCapAndReportsTruncation()
    {
        await using var project = await CreateProjectAsync();
        await SeedAsync(project);
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(
            admin,
            project.Id,
            "SELECT id FROM sql_items ORDER BY id",
            maxRows: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("rowCount").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("INSERT INTO sql_items (id, nombre) VALUES (99, 'mal')")]
    [InlineData("UPDATE sql_items SET nombre = 'mal'")]
    [InlineData("DELETE FROM sql_items")]
    [InlineData("CREATE TABLE sql_bad (id int4)")]
    [InlineData("SELECT 1; DROP TABLE sql_items")]
    [InlineData("SELECT nextval('sql_seq')")]
    [InlineData("SELECT set_config('search_path', 'pg_catalog', false)")]
    public async Task WriteOrInjectedStatement_IsRejectedWithoutTouchingData(string sql)
    {
        await using var project = await CreateProjectAsync();
        await SeedAsync(project);
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(admin, project.Id, sql);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
        Assert.Equal(2, await CountRowsAsync(project));
    }

    [Fact]
    public async Task UnknownTable_ReturnsSafeValidationError()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(admin, project.Id, "SELECT * FROM tabla_inexistente");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT * FROM tabla_inexistente", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowQuery_IsCancelledByStatementTimeout()
    {
        await using var project = await CreateProjectAsync();
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(
            admin,
            project.Id,
            "SELECT pg_sleep(3)",
            timeoutMs: 250);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CONFLICT", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Query_IsAuditedWithoutSqlOrSecrets()
    {
        await using var project = await CreateProjectAsync();
        await SeedAsync(project);
        using var admin = factory.CreateAdminClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await ExecuteAsync(admin, project.Id, "SELECT id FROM sql_items WHERE id = 1")).StatusCode);

        await using var control = new NpgsqlConnection(factory.ControlConnectionString);
        await control.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT details::text
            FROM audit_entries
            WHERE target_id = @id AND action = 'sql.query'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            control);
        command.Parameters.AddWithValue("id", project.Id.ToString());
        var details = (string)(await command.ExecuteScalarAsync())!;

        Assert.Contains("rowCount", details, StringComparison.Ordinal);
        Assert.Contains("sqlFingerprint", details, StringComparison.Ordinal);
        Assert.DoesNotContain("sql_items", details, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_IsScopedToTheRequestedProject()
    {
        await using var own = await CreateProjectAsync();
        await using var other = await CreateProjectAsync();
        await SeedAsync(other);
        using var admin = factory.CreateAdminClient();

        var response = await ExecuteAsync(admin, own.Id, "SELECT id FROM sql_items");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    private async Task<TestProject> CreateProjectAsync()
    {
        var slug = "sql-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "SQL editor test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetGuid();
        return new TestProject(factory, id, slug);
    }

    private async Task OpenAndSeedAsync(TestProject project, Func<NpgsqlConnection, Task> action)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ProvisioningConnectionString)
        {
            Database = project.DatabaseName
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await action(connection);
    }

    private Task SeedAsync(TestProject project) => OpenAndSeedAsync(project, async connection =>
    {
        // Act as the project owner, exactly like the P1.1 schema administration path, so the
        // table is owned by the role the SQL editor uses.
        await using (var role = new NpgsqlCommand($"SET ROLE \"{project.OwnerRole}\"", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using (var create = new NpgsqlCommand(
            $"CREATE TABLE public.{Table} (id int4 PRIMARY KEY, nombre text NOT NULL)", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO public.{Table} (id, nombre) VALUES (1, 'alfa'), (2, 'beta')", connection))
        {
            await insert.ExecuteNonQueryAsync();
        }
    });

    private async Task<int> CountRowsAsync(TestProject project)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ProvisioningConnectionString)
        {
            Database = project.DatabaseName
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*)::int FROM public.{Table}", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static Task<HttpResponseMessage> ExecuteAsync(
        HttpClient admin,
        Guid projectId,
        string sql,
        int? maxRows = null,
        int? timeoutMs = null) =>
        admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/sql/query",
            new { sql, maxRows, timeoutMs });

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
