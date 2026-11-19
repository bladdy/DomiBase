using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RuntimeCollection : ICollectionFixture<RuntimeProjectFixture>
{
    public const string Name = "Runtime";
}

/// <summary>
/// Creates one real project, a public API key and a set of tables with RLS/grants.
/// Two test classes share it so provisioning happens once. Tests use unique values
/// and never assume the tables are empty.
/// </summary>
public sealed class RuntimeProjectFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public string OwnerRole => ProjectNaming.ToOwnerRole(Slug);

    public string AnonymousRole => ProjectNaming.ToAnonymousRole(Slug);

    public string RuntimeRole => ProjectNaming.ToRuntimeRole(Slug);

    public async Task InitializeAsync()
    {
        Slug = "it-" + Guid.NewGuid().ToString("N")[..10];

        using var admin = Factory.CreateAdminClient();

        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Runtime Integration Test" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "runtime-integration" });
        Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);

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

    private async Task CreateSchemaAsync()
    {
        await using var connection = await OpenProjectDatabaseAsync();

        await ExecuteAsync(connection, $"SET ROLE {Quote(OwnerRole)}");

        var anon = Quote(AnonymousRole);
        var projectId = ProjectId.ToString();
        var otherProjectId = Guid.NewGuid().ToString();

        var ddl = $"""
            CREATE TABLE public.contactos (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                nombre text NOT NULL,
                email text UNIQUE,
                estado text NOT NULL DEFAULT 'nuevo',
                edad int4,
                activo boolean NOT NULL DEFAULT true,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE public.contactos ENABLE ROW LEVEL SECURITY;
            GRANT SELECT, INSERT, UPDATE, DELETE ON public.contactos TO {anon};
            CREATE POLICY contactos_select ON public.contactos FOR SELECT TO {anon} USING (true);
            CREATE POLICY contactos_insert ON public.contactos FOR INSERT TO {anon} WITH CHECK (true);
            CREATE POLICY contactos_update ON public.contactos FOR UPDATE TO {anon} USING (true) WITH CHECK (true);
            CREATE POLICY contactos_delete ON public.contactos FOR DELETE TO {anon} USING (true);

            CREATE TABLE public.solo_lectura (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                valor text NOT NULL
            );
            ALTER TABLE public.solo_lectura ENABLE ROW LEVEL SECURITY;
            GRANT SELECT ON public.solo_lectura TO {anon};
            CREATE POLICY solo_lectura_select ON public.solo_lectura FOR SELECT TO {anon} USING (true);

            CREATE TABLE public.privado (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                secreto text NOT NULL
            );
            ALTER TABLE public.privado ENABLE ROW LEVEL SECURITY;
            GRANT SELECT, INSERT ON public.privado TO {anon};

            CREATE TABLE public.tenant_items (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                project_id uuid NOT NULL,
                nombre text NOT NULL
            );
            ALTER TABLE public.tenant_items ENABLE ROW LEVEL SECURITY;
            GRANT SELECT, INSERT ON public.tenant_items TO {anon};
            CREATE POLICY tenant_select ON public.tenant_items FOR SELECT TO {anon}
                USING (project_id = current_setting('domibase.project_id', true)::uuid);
            CREATE POLICY tenant_insert ON public.tenant_items FOR INSERT TO {anon}
                WITH CHECK (project_id = current_setting('domibase.project_id', true)::uuid);
            INSERT INTO public.tenant_items (project_id, nombre) VALUES
                ('{projectId}', 'propio'),
                ('{otherProjectId}', 'ajeno');

            CREATE TABLE public.sin_rls (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                valor text NOT NULL
            );
            GRANT SELECT ON public.sin_rls TO {anon};
            INSERT INTO public.sin_rls (valor) VALUES ('expuesto');

            CREATE TABLE public.interno (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                valor text NOT NULL
            );
            INSERT INTO public.interno (valor) VALUES ('privado');
            """;

        await ExecuteAsync(connection, ddl);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
