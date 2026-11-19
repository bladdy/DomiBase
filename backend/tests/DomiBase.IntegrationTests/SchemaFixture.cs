using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SchemaCollection : ICollectionFixture<SchemaFixture>
{
    public const string Name = "Schema";
}

/// <summary>
/// Provisioned project with known public objects and decoy internal objects. Used to
/// verify that the Control Plane schema inspector reads live PostgreSQL catalog data
/// without exposing schemas other than <c>public</c>.
/// </summary>
public sealed class SchemaFixture : IAsyncLifetime
{
    public const string TableName = "schema_probe_table";
    public const string ViewName = "schema_probe_view";
    public const string MaterializedViewName = "schema_probe_matview";
    public const string FunctionName = "schema_probe_fn";
    public const string InternalSchema = "internal_probe";
    public const string HiddenTableName = "hidden_table";
    public const string HiddenViewName = "hidden_view";
    public const string HiddenFunctionName = "hidden_fn";

    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public async Task InitializeAsync()
    {
        Slug = "schema-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();
        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Schema Integration Test" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var builder = new NpgsqlConnectionStringBuilder(Factory.ProvisioningConnectionString)
        {
            Database = DatabaseName
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(connection, $"CREATE TABLE public.{TableName} (id integer PRIMARY KEY)");
        await ExecuteAsync(connection, $"CREATE VIEW public.{ViewName} AS SELECT id FROM public.{TableName}");
        await ExecuteAsync(
            connection,
            $"CREATE MATERIALIZED VIEW public.{MaterializedViewName} AS SELECT id FROM public.{TableName}");
        await ExecuteAsync(
            connection,
            $"CREATE FUNCTION public.{FunctionName}(value text) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT value $$");

        await ExecuteAsync(connection, $"CREATE SCHEMA {InternalSchema}");
        await ExecuteAsync(connection, $"CREATE TABLE {InternalSchema}.{HiddenTableName} (id integer)");
        await ExecuteAsync(
            connection,
            $"CREATE VIEW {InternalSchema}.{HiddenViewName} AS SELECT id FROM {InternalSchema}.{HiddenTableName}");
        await ExecuteAsync(
            connection,
            $"CREATE FUNCTION {InternalSchema}.{HiddenFunctionName}() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 1 $$");
    }

    public async Task DisposeAsync()
    {
        try
        {
            await new ProjectCleanup(Factory.ProvisioningConnectionString, Factory.ControlConnectionString)
                .DropProjectAsync(Slug, ProjectId);
        }
        finally
        {
            Factory.Dispose();
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
