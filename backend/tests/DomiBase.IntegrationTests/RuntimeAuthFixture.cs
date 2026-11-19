using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RuntimeAuthCollection : ICollectionFixture<RuntimeAuthFixture>
{
    public const string Name = "RuntimeAuth";
}

/// <summary>
/// Creates one real project with a table protected by RLS for authenticated users.
/// Anonymous requests see only rows with project_id; authenticated users see their own rows.
/// </summary>
public sealed class RuntimeAuthFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public string OwnerRole => ProjectNaming.ToOwnerRole(Slug);

    public string AnonymousRole => ProjectNaming.ToAnonymousRole(Slug);

    public string AuthenticatedRole => ProjectNaming.ToAuthenticatedRole(Slug);

    public async Task InitializeAsync()
    {
        Slug = "auth-runtime-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();

        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Runtime Auth Integration Test" });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "runtime-auth-integration" });
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

    public HttpClient CreateRuntimeClient() => Factory.CreateClient();

    public HttpClient CreateRuntimeClientWithKey()
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
        var authenticated = Quote(AuthenticatedRole);

        var ddl = $"""
            CREATE TABLE public.tareas (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                owner_id uuid NOT NULL DEFAULT current_setting('domibase.user_id', true)::uuid,
                titulo text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE public.tareas ENABLE ROW LEVEL SECURITY;
            GRANT SELECT, INSERT ON public.tareas TO {anon}, {authenticated};
            CREATE POLICY tareas_anon_select ON public.tareas FOR SELECT TO {anon}
                USING (false);
            CREATE POLICY tareas_anon_insert ON public.tareas FOR INSERT TO {anon}
                WITH CHECK (false);
            CREATE POLICY tareas_auth_select ON public.tareas FOR SELECT TO {authenticated}
                USING (owner_id = current_setting('domibase.user_id', true)::uuid);
            CREATE POLICY tareas_auth_insert ON public.tareas FOR INSERT TO {authenticated}
                WITH CHECK (owner_id = current_setting('domibase.user_id', true)::uuid);
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
