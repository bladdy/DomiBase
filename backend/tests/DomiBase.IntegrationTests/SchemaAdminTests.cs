using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace DomiBase.IntegrationTests;

[Collection(SchemaAdminCollection.Name)]
public sealed class SchemaAdminTests(SchemaAdminFixture fixture)
{
    [Fact]
    public async Task DdlLifecycle_CreatesAltersAndDropsRealPostgreSqlObjects()
    {
        var table = SchemaAdminFixture.UniqueName("items_");
        var index = table + "_titulo_idx";
        using var admin = fixture.Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables",
            new
            {
                name = table,
                columns = new object[]
                {
                    new { name = "id", type = "uuid", nullable = false, primaryKey = true, @default = "gen_random_uuid()" },
                    new { name = "nombre", type = "text", nullable = false },
                    new { name = "created_at", type = "timestamptz", nullable = false, @default = "now()" }
                }
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        await using (var database = await fixture.OpenProjectDatabaseAsync())
        {
            Assert.True(await TableExistsAsync(database, table));
            Assert.True(await TableHasRlsAsync(database, table));
            Assert.Equal(3, await CountColumnsAsync(database, table));
        }

        var rename = await admin.PatchAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/columns/nombre",
            new { name = "titulo" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var addColumn = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/columns",
            new { name = "activo", type = "boolean", nullable = false, @default = true });
        Assert.Equal(HttpStatusCode.OK, addColumn.StatusCode);

        var createIndex = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/indexes",
            new { name = index, columns = new[] { "titulo" }, unique = true });
        Assert.Equal(HttpStatusCode.OK, createIndex.StatusCode);

        await using (var database = await fixture.OpenProjectDatabaseAsync())
        {
            Assert.True(await ColumnExistsAsync(database, table, "titulo"));
            Assert.False(await ColumnExistsAsync(database, table, "nombre"));
            Assert.True(await ColumnExistsAsync(database, table, "activo"));
            Assert.True(await IndexExistsAsync(database, table, index));
        }

        var wrongTableDrop = await admin.DeleteAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/otra_tabla/indexes/{index}");
        Assert.Equal(HttpStatusCode.BadRequest, wrongTableDrop.StatusCode);

        var dropIndex = await admin.DeleteAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/indexes/{index}");
        Assert.Equal(HttpStatusCode.OK, dropIndex.StatusCode);

        var dropColumn = await admin.DeleteAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/columns/activo");
        Assert.Equal(HttpStatusCode.OK, dropColumn.StatusCode);

        var dropTable = await admin.DeleteAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}");
        Assert.Equal(HttpStatusCode.OK, dropTable.StatusCode);

        await using (var database = await fixture.OpenProjectDatabaseAsync())
        {
            Assert.False(await TableExistsAsync(database, table));
        }
    }

    [Fact]
    public async Task Policy_ControlsRuntimeAccessAndTenantTemplateIsolatesRows()
    {
        var table = SchemaAdminFixture.UniqueName("tenant_");
        var policy = table + "_policy";
        using var admin = fixture.Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables",
            new
            {
                name = table,
                columns = new object[]
                {
                    new { name = "id", type = "uuid", nullable = false, primaryKey = true, @default = "gen_random_uuid()" },
                    new { name = "project_id", type = "uuid", nullable = false },
                    new { name = "nombre", type = "text", nullable = false }
                }
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var key = await fixture.CreatePublicKeyAsync(admin);
        using var runtime = fixture.CreateRuntimeClient(key);

        // RLS is enabled with no grants: the table is invisible through the public API.
        var beforePolicy = await runtime.GetAsync($"/rest/v1/{table}");
        Assert.NotEqual(HttpStatusCode.OK, beforePolicy.StatusCode);

        var createPolicy = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/policies",
            new
            {
                name = policy,
                commands = new[] { "SELECT", "INSERT" },
                roles = new[] { "anon" }
            }.WithPolicyExpressions());
        Assert.Equal(HttpStatusCode.OK, createPolicy.StatusCode);

        var listed = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/policies");
        Assert.Equal(1, listed.GetArrayLength());
        var listedPolicy = listed[0];
        Assert.Equal(policy, listedPolicy.GetProperty("name").GetString());
        Assert.Contains("anon", listedPolicy.GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("SELECT", listedPolicy.GetProperty("commands").EnumerateArray().Select(x => x.GetString()));

        var insert = await runtime.PostAsJsonAsync(
            $"/rest/v1/{table}",
            new { project_id = fixture.ProjectId, nombre = "propio" });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        await using (var database = await fixture.OpenProjectDatabaseAsync())
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO public.\"{table}\" (project_id, nombre) VALUES (@id, 'ajeno')",
                database);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            await command.ExecuteNonQueryAsync();
        }

        var filtered = await runtime.GetFromJsonAsync<JsonElement>($"/rest/v1/{table}");
        Assert.Equal(1, filtered.GetProperty("count").GetInt32());
        Assert.Equal("propio", filtered.GetProperty("data")[0].GetProperty("nombre").GetString());

        var alter = await admin.PatchAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/policies/{policy}",
            new { name = policy, commands = new[] { "SELECT", "INSERT" }, roles = new[] { "anon" } }
                .WithPolicyExpressions("true"));
        Assert.Equal(HttpStatusCode.OK, alter.StatusCode);

        var visible = await runtime.GetFromJsonAsync<JsonElement>($"/rest/v1/{table}");
        Assert.Equal(2, visible.GetProperty("count").GetInt32());

        var drop = await admin.DeleteAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables/{table}/policies/{policy}");
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);

        // Grants remain, but RLS with no policy denies every row: the response is empty.
        var afterDrop = await runtime.GetFromJsonAsync<JsonElement>($"/rest/v1/{table}");
        Assert.Equal(0, afterDrop.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task InvalidIdentifiersTypesAndExpressions_AreRejectedWithoutTouchingData()
    {
        var sentinel = SchemaAdminFixture.UniqueName("sentinel_");
        using var admin = fixture.Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables",
            new { name = sentinel, columns = new[] { new { name = "id", type = "int4" } } });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var cases = new (string Url, object Body)[]
        {
            ($"/api/v1/projects/{fixture.ProjectId}/schema/tables",
                new { name = "bad\"; DROP TABLE sentinel; --", columns = new[] { new { name = "id", type = "int4" } } }),
            ($"/api/v1/projects/{fixture.ProjectId}/schema/tables",
                new { name = " otra", columns = new[] { new { name = "id", type = "int4" } } }),
            ($"/api/v1/projects/{fixture.ProjectId}/schema/tables/{sentinel}/columns",
                new { name = "id", type = "int4); DROP TABLE sentinel; --" }),
            ($"/api/v1/projects/{fixture.ProjectId}/schema/tables/{sentinel}/indexes",
                new { name = "idx", columns = new[] { "id); DROP TABLE sentinel; --" } }),
            ($"/api/v1/projects/{fixture.ProjectId}/schema/tables/{sentinel}/policies",
                new { name = "politica", commands = new[] { "SELECT" }, roles = new[] { "anon" } }
                    .WithPolicyExpressions("true; DROP TABLE sentinel; --"))
        };

        foreach (var (url, body) in cases)
        {
            var response = await admin.PostAsJsonAsync(url, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
        }

        await using var database = await fixture.OpenProjectDatabaseAsync();
        Assert.True(await TableExistsAsync(database, sentinel));
    }

    [Fact]
    public async Task UnknownProject_ReturnsNotFound()
    {
        using var admin = fixture.Factory.CreateAdminClient();
        var missing = Guid.NewGuid();

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{missing}/schema/tables",
            new { name = "whatever", columns = new[] { new { name = "id", type = "int4" } } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DdlOperations_AreAudited()
    {
        var table = SchemaAdminFixture.UniqueName("audit_");
        using var admin = fixture.Factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables",
            new { name = table, columns = new[] { new { name = "id", type = "int4" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var control = new NpgsqlConnection(fixture.Factory.ControlConnectionString);
        await control.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM audit_entries
            WHERE target_id = @id AND action = 'schema.table.create'
            """,
            control);
        command.Parameters.AddWithValue("id", fixture.ProjectId.ToString());

        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0);
    }

    [Fact]
    public async Task DdlOnAnotherProjectTable_IsRejectedAndDoesNotModifyIt()
    {
        var table = SchemaAdminFixture.UniqueName("isolated_");
        using var admin = fixture.Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/schema/tables",
            new { name = table, columns = new[] { new { name = "id", type = "int4" } } });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var otherSlug = SchemaAdminFixture.UniqueName("otro-");
        Guid otherId = Guid.Empty;
        try
        {
            var other = await admin.PostAsJsonAsync(
                "/api/v1/projects",
                new { slug = otherSlug, name = "Isolation test" });
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            using var otherJson = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
            otherId = otherJson.RootElement.GetProperty("id").GetGuid();

            var attempt = await admin.PostAsJsonAsync(
                $"/api/v1/projects/{otherId}/schema/tables/{table}/columns",
                new { name = "intruso", type = "text" });
            Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);

            await using var database = await fixture.OpenProjectDatabaseAsync();
            Assert.False(await ColumnExistsAsync(database, table, "intruso"));
            Assert.True(await ColumnExistsAsync(database, table, "id"));
        }
        finally
        {
            if (otherId != Guid.Empty)
            {
                await new ProjectCleanup(fixture.Factory.ProvisioningConnectionString, fixture.Factory.ControlConnectionString)
                    .DropProjectAsync(otherSlug, otherId);
            }
        }
    }

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
            """
            SELECT c.relrowsecurity FROM pg_class c
            WHERE c.oid = to_regclass(@name)::oid
            """,
            connection);
        command.Parameters.AddWithValue("name", "public." + table);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<int> CountColumnsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::int FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection connection, string table, string column)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection connection, string table, string index)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = @table AND indexname = @index
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("index", index);
        return await command.ExecuteScalarAsync() is not null;
    }
}

/// <summary>
/// The public contract uses <c>using</c>/<c>withCheck</c>; this helper keeps anonymous test
/// payloads readable while emitting the exact JSON property names the API expects.
/// </summary>
internal static class PolicyPayloadExtensions
{
    public static object WithPolicyExpressions(this object payload, string expression = SchemaAdminTestExpressions.Tenant)
    {
        var json = JsonSerializer.SerializeToNode(payload)!.AsObject();
        json["using"] = expression;
        json["withCheck"] = expression == SchemaAdminTestExpressions.Tenant
            ? "project_id = current_setting('domibase.project_id', true)::uuid"
            : expression;
        return json;
    }
}

internal static class SchemaAdminTestExpressions
{
    public const string Tenant = "project_id = current_setting('domibase.project_id', true)::uuid";
}
