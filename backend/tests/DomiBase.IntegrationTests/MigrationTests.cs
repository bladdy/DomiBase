using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Each test provisions its own project so migration version ordering is never affected by
/// test execution order. The history lives in that project database only.
/// </summary>
public sealed class MigrationTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task ApplyMigration_CreatesObjectsAndHistory_ThenReapplyIsIdempotent()
    {
        await using var project = await CreateProjectAsync();
        var table = "mig_items";
        var policy = table + "_select";
        using var admin = factory.CreateAdminClient();

        var response = await ApplyAsync(
            admin, project.Id, 1, "initial_setup",
            CreateTableOp(table),
            PolicyOp(table, policy, "true"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        string checksum;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("initial_setup", root.GetProperty("name").GetString());
            Assert.False(root.GetProperty("alreadyApplied").GetBoolean());
            checksum = root.GetProperty("checksum").GetString()!;
            Assert.StartsWith("v1.", checksum);
        }

        await using (var database = await OpenDatabaseAsync(project))
        {
            Assert.True(await TableExistsAsync(database, table));
            Assert.True(await TableHasRlsAsync(database, table));
            Assert.True(await PolicyExistsAsync(database, table, policy));
            Assert.Equal(1, await HistoryCountAsync(database, 1));
        }

        var list = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/projects/{project.Id}/migrations");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(checksum, list[0].GetProperty("checksum").GetString());
        Assert.Equal(2, list[0].GetProperty("operationCount").GetInt32());

        var replay = await ApplyAsync(
            admin, project.Id, 1, "initial_setup",
            CreateTableOp(table),
            PolicyOp(table, policy, "true"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("alreadyApplied").GetBoolean());
        }

        await using (var database = await OpenDatabaseAsync(project))
        {
            Assert.Equal(1, await HistoryCountAsync(database, 1));
        }
    }

    [Fact]
    public async Task ChangedDefinitionAndOutOfOrderVersions_AreRejected()
    {
        await using var project = await CreateProjectAsync();
        var table = "mig_version_probe";
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(admin, project.Id, 20, "version_base", CreateTableOp(table))).StatusCode);

        var changedName = await ApplyAsync(admin, project.Id, 20, "renamed_definition", CreateTableOp(table));
        Assert.Equal(HttpStatusCode.Conflict, changedName.StatusCode);
        Assert.Equal("CONFLICT", await ReadErrorCodeAsync(changedName));

        var changedOperations = await ApplyAsync(admin, project.Id, 20, "version_base", CreateTableOp("mig_other_table"));
        Assert.Equal(HttpStatusCode.Conflict, changedOperations.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(admin, project.Id, 21, "next_version", CreateTableOp("mig_next_table"))).StatusCode);

        var outOfOrder = await ApplyAsync(admin, project.Id, 19, "older_version", CreateTableOp("mig_older_table"));
        Assert.Equal(HttpStatusCode.Conflict, outOfOrder.StatusCode);

        await using var database = await OpenDatabaseAsync(project);
        Assert.False(await TableExistsAsync(database, "mig_older_table"));
        Assert.False(await TableExistsAsync(database, "mig_other_table"));
    }

    [Fact]
    public async Task InvalidOperation_IsRejectedBeforeAnyDdlOrHistoryWrite()
    {
        await using var project = await CreateProjectAsync();
        const int version = 30;
        using var admin = factory.CreateAdminClient();

        var response = await ApplyAsync(
            admin,
            project.Id,
            version,
            "bad_operation",
            new { type = "create_table", name = "bad\"; DROP TABLE mig_sentinel; --", columns = new[] { new { name = "id", type = "int4" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));

        await using var database = await OpenDatabaseAsync(project);
        Assert.False(await TableExistsAsync(database, "mig_sentinel"));
        Assert.Equal(0, await HistoryCountAsync(database, version));
    }

    [Fact]
    public async Task MigrationHistory_IsInvisibleToRuntimeAndSchemaInspector()
    {
        await using var project = await CreateProjectAsync();
        const int version = 40;
        var table = "mig_hidden_history";
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(admin, project.Id, version, "hidden_history", CreateTableOp(table))).StatusCode);

        var tables = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/projects/{project.Id}/schema/tables");
        var names = tables.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("applied", names);
        Assert.DoesNotContain("domibase_migrations", names);

        var key = await CreatePublicKeyAsync(admin, project.Id);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", key);
        using (client)
        {
            var direct = await client.GetAsync("/rest/v1/applied");
            Assert.NotEqual(HttpStatusCode.OK, direct.StatusCode);
        }
    }

    [Fact]
    public async Task MigrationHistory_IsScopedToItsProject()
    {
        await using var own = await CreateProjectAsync();
        await using var other = await CreateProjectAsync();
        var otherTable = "mig_scoped_table";
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(admin, other.Id, 50, "scoped", CreateTableOp(otherTable))).StatusCode);

        await using (var otherDatabase = await OpenDatabaseAsync(other))
        {
            Assert.True(await TableExistsAsync(otherDatabase, otherTable));
            Assert.Equal(1, await HistoryCountAsync(otherDatabase, 50));
        }

        await using var ownDatabase = await OpenDatabaseAsync(own);
        Assert.False(await TableExistsAsync(ownDatabase, otherTable));
    }

    private async Task<TestProject> CreateProjectAsync()
    {
        var slug = "mig-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Migration test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetGuid();
        return new TestProject(factory, id, slug, ProjectNaming.ToDatabaseName(slug));
    }

    private async Task<NpgsqlConnection> OpenDatabaseAsync(TestProject project)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ProvisioningConnectionString)
        {
            Database = project.DatabaseName
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> CreatePublicKeyAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/keys",
            new { label = "migration-test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("key").GetString()!;
    }

    private static Task<HttpResponseMessage> ApplyAsync(
        HttpClient admin,
        Guid projectId,
        int version,
        string name,
        params object[] operations) =>
        admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/migrations",
            new { version, name, operations });

    private static object CreateTableOp(string table) => new
    {
        type = "create_table",
        name = table,
        columns = new object[]
        {
            new { name = "id", type = "uuid", nullable = false, primaryKey = true, @default = "gen_random_uuid()" },
            new { name = "nombre", type = "text", nullable = false }
        }
    };

    private static object PolicyOp(string table, string name, string expression) => new
    {
        type = "create_policy",
        table,
        name,
        commands = new[] { "SELECT" },
        roles = new[] { "anon" },
        @using = expression,
        withCheck = expression
    };

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", "public." + table);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> TableHasRlsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand(
            "SELECT relrowsecurity FROM pg_class WHERE oid = to_regclass(@name)::oid", connection);
        command.Parameters.AddWithValue("name", "public." + table);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> PolicyExistsAsync(NpgsqlConnection connection, string table, string policy)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_policies WHERE schemaname = 'public' AND tablename = @table AND policyname = @policy",
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("policy", policy);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<int> HistoryCountAsync(NpgsqlConnection connection, int version)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('domibase_migrations.applied') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync() is not true)
        {
            return 0;
        }

        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM domibase_migrations.applied WHERE version = @version", connection);
        command.Parameters.AddWithValue("version", version);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestProject(
        DomiBaseWebApplicationFactory factory,
        Guid id,
        string slug,
        string databaseName) : IAsyncDisposable
    {
        public Guid Id { get; } = id;

        public string Slug { get; } = slug;

        public string DatabaseName { get; } = databaseName;

        public ValueTask DisposeAsync() => new(
            new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString)
                .DropProjectAsync(Slug, Id));
    }
}
