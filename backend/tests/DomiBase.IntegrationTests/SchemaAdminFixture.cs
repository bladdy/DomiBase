using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SchemaAdminCollection : ICollectionFixture<SchemaAdminFixture>
{
    public const string Name = "SchemaAdmin";
}

/// <summary>
/// A freshly provisioned project used to verify that administrative DDL runs against the
/// correct project database through the privileged owner-role path.
/// </summary>
public sealed class SchemaAdminFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public async Task InitializeAsync()
    {
        Slug = "schadmin-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Schema Administration Test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ProjectId = json.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Ready", json.RootElement.GetProperty("status").GetString());
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

    public async Task<NpgsqlConnection> OpenProjectDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Factory.ProvisioningConnectionString)
        {
            Database = DatabaseName
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public HttpClient CreateRuntimeClient(string key)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", key);
        return client;
    }

    public async Task<string> CreatePublicKeyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "schema-admin-test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("key").GetString()!;
    }

    public static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];
}
