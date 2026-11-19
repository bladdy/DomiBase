using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RealtimeCollection : ICollectionFixture<RealtimeFixture>
{
    public const string Name = "Realtime";
}

public sealed class RealtimeFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public string OwnerRole => ProjectNaming.ToOwnerRole(Slug);

    public string AnonymousRole => ProjectNaming.ToAnonymousRole(Slug);

    public string Url => "http://localhost/realtime/v1";

    public async Task InitializeAsync()
    {
        Slug = "realtime-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();

        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Realtime Integration Test" });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "realtime-integration" });
        Assert.Equal(System.Net.HttpStatusCode.Created, keyResponse.StatusCode);

        using (var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
        {
            ApiKey = keyJson.RootElement.GetProperty("key").GetString()!;
        }

        await CreateSchemaAsync();
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

    public HttpClient CreateRuntimeClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", ApiKey);
        return client;
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = await OpenProjectDatabaseAsync();
        await ExecuteAsync(connection, $"SET ROLE {Quote(OwnerRole)}");

        var anon = Quote(AnonymousRole);

        var ddl = $"""
            CREATE TABLE public.eventos (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                nombre text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE public.eventos ENABLE ROW LEVEL SECURITY;
            GRANT SELECT, INSERT ON public.eventos TO {anon};
            CREATE POLICY eventos_select ON public.eventos FOR SELECT TO {anon} USING (true);
            CREATE POLICY eventos_insert ON public.eventos FOR INSERT TO {anon} WITH CHECK (true);
            """;

        await ExecuteAsync(connection, ddl);
    }

    private async Task<NpgsqlConnection> OpenProjectDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Factory.ProvisioningConnectionString)
        {
            Database = DatabaseName
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
