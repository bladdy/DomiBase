using System.Net;
using System.Text.Json;
using Npgsql;

namespace DomiBase.IntegrationTests;

[Collection(SchemaCollection.Name)]
public sealed class SchemaControlTests(SchemaFixture fixture)
{
    [Fact]
    public async Task ListsPublicTablesViewsAndFunctionsFromLiveCatalog()
    {
        using var admin = fixture.Factory.CreateAdminClient();

        var tables = await ReadNamesAsync(admin, "tables");
        var views = await ReadNamesAsync(admin, "views");
        var functions = await ReadFunctionsAsync(admin);

        Assert.Contains(SchemaFixture.TableName, tables);
        Assert.DoesNotContain(SchemaFixture.HiddenTableName, tables);
        Assert.DoesNotContain("pg_class", tables);

        Assert.Contains(SchemaFixture.ViewName, views);
        Assert.Contains(SchemaFixture.MaterializedViewName, views);
        Assert.DoesNotContain(SchemaFixture.HiddenViewName, views);

        Assert.Contains(
            functions,
            fn => fn.Name == SchemaFixture.FunctionName
                  && fn.Arguments == "value text"
                  && fn.ReturnType == "text");
        Assert.DoesNotContain(functions, fn => fn.Name == SchemaFixture.HiddenFunctionName);
    }

    [Fact]
    public async Task ClientSuppliedSchemaAndIdentifiersAreIgnored()
    {
        using var admin = fixture.Factory.CreateAdminClient();

        var tables = await ReadNamesAsync(admin, "tables?schema=pg_catalog&table=pg_class&identifier=pg_class");
        var views = await ReadNamesAsync(admin, "views?schema=internal_probe");

        Assert.Contains(SchemaFixture.TableName, tables);
        Assert.DoesNotContain("pg_class", tables);
        Assert.Contains(SchemaFixture.ViewName, views);
        Assert.DoesNotContain(SchemaFixture.HiddenViewName, views);
    }

    [Fact]
    public async Task UnknownProject_ReturnsNotFound()
    {
        using var admin = fixture.Factory.CreateAdminClient();
        var missingId = Guid.NewGuid();

        var response = await admin.GetAsync(
            $"/api/v1/projects/{missingId}/schema/tables");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NOT_FOUND", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SchemaReads_AreAudited()
    {
        using var admin = fixture.Factory.CreateAdminClient();
        var response = await admin.GetAsync($"/api/v1/projects/{fixture.ProjectId}/schema/tables");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var control = new NpgsqlConnection(fixture.Factory.ControlConnectionString);
        await control.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM audit_entries
            WHERE target_id = @projectId AND action = 'schema.tables'
            """,
            control);
        command.Parameters.AddWithValue("projectId", fixture.ProjectId.ToString());

        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.True(count > 0, "schema.tables reads must produce an audit entry.");
    }

    private async Task<List<string>> ReadNamesAsync(HttpClient client, string suffix)
    {
        using var json = JsonDocument.Parse(
            await client.GetStringAsync($"/api/v1/projects/{fixture.ProjectId}/schema/{suffix}"));
        return json.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToList();
    }

    private async Task<List<(string Name, string Arguments, string ReturnType)>> ReadFunctionsAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(
            await client.GetStringAsync($"/api/v1/projects/{fixture.ProjectId}/schema/functions"));
        return json.RootElement.EnumerateArray()
            .Select(item => (
                item.GetProperty("name").GetString()!,
                item.GetProperty("arguments").GetString()!,
                item.GetProperty("returnType").GetString()!))
            .ToList();
    }
}
