using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DomiBase.Application.Runtime;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DomiBase.IntegrationTests;

[Collection(RuntimeCollection.Name)]
public class RuntimeSecurityTests(RuntimeProjectFixture fixture)
{
    [Fact]
    public async Task MissingInvalidAndRevokedKeys_AreRejected()
    {
        using var anonymous = fixture.Factory.CreateClient();

        var missing = await anonymous.GetAsync("/rest/v1/contactos");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("MISSING_API_KEY", await ErrorCodeAsync(missing));

        var invalid = await SendWithKeyAsync("db_public_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal("INVALID_API_KEY", await ErrorCodeAsync(invalid));

        var shortKey = await SendWithKeyAsync("nope");
        Assert.Equal(HttpStatusCode.Unauthorized, shortKey.StatusCode);

        var secretKind = await SendWithKeyAsync("db_secret_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, secretKind.StatusCode);

        using var admin = fixture.Factory.CreateAdminClient();
        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{fixture.ProjectId}/keys",
            new { label = "to-revoke" });
        Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);

        Guid keyId;
        string keyValue;
        using (var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
        {
            keyId = keyJson.RootElement.GetProperty("id").GetGuid();
            keyValue = keyJson.RootElement.GetProperty("key").GetString()!;
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await SendWithKeyAsync(keyValue)).StatusCode);

        var revoke = await admin.DeleteAsync($"/api/v1/projects/{fixture.ProjectId}/keys/{keyId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var revoked = await SendWithKeyAsync(keyValue);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("INVALID_API_KEY", await ErrorCodeAsync(revoked));

        var body = await revoked.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_hash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExposureRules_RespectGrantsAndRowLevelSecurity()
    {
        using var client = fixture.CreateRuntimeClientWithKey();

        var internalTable = await client.GetAsync("/rest/v1/interno");
        Assert.Equal(HttpStatusCode.NotFound, internalTable.StatusCode);
        Assert.Equal("TABLE_NOT_FOUND", await ErrorCodeAsync(internalTable));

        var noRls = await client.GetAsync("/rest/v1/sin_rls");
        Assert.Equal(HttpStatusCode.NotFound, noRls.StatusCode);
        Assert.Equal("TABLE_NOT_FOUND", await ErrorCodeAsync(noRls));

        var unknown = await client.GetAsync("/rest/v1/does_not_exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // The anon role has SELECT/INSERT grants but no policy: SELECT sees zero rows,
        // INSERT is denied by RLS.
        var privateSelect = await client.GetAsync("/rest/v1/privado");
        Assert.Equal(HttpStatusCode.OK, privateSelect.StatusCode);
        using (var json = await ReadJsonAsync(privateSelect))
        {
            Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        }

        var privateInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/privado",
            new { secreto = "no permitido" });
        Assert.Equal(HttpStatusCode.Forbidden, privateInsert.StatusCode);
        Assert.Equal("FORBIDDEN", await ErrorCodeAsync(privateInsert));
    }

    [Fact]
    public async Task RlsUsesTransactionClaimsForRowIsolation()
    {
        using var client = fixture.CreateRuntimeClientWithKey();

        var ownRows = await client.GetAsync("/rest/v1/tenant_items?select=nombre&f=nombre.eq.propio");
        Assert.Equal(HttpStatusCode.OK, ownRows.StatusCode);
        using (var json = await ReadJsonAsync(ownRows))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        }

        var foreignRows = await client.GetAsync("/rest/v1/tenant_items?f=nombre.eq.ajeno");
        Assert.Equal(HttpStatusCode.OK, foreignRows.StatusCode);
        using (var json = await ReadJsonAsync(foreignRows))
        {
            Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        }

        // A row for another project is rejected by the RLS WITH CHECK expression.
        var foreignInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/tenant_items",
            new { project_id = Guid.NewGuid(), nombre = "intruso" });
        Assert.Equal(HttpStatusCode.Forbidden, foreignInsert.StatusCode);
        Assert.Equal("FORBIDDEN", await ErrorCodeAsync(foreignInsert));

        var name = "nuevo-" + Guid.NewGuid().ToString("N")[..8];
        var ownInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/tenant_items?select=project_id,nombre",
            new { project_id = fixture.ProjectId, nombre = name });
        Assert.Equal(HttpStatusCode.Created, ownInsert.StatusCode);

        using (var json = await ReadJsonAsync(ownInsert))
        {
            var row = json.RootElement.GetProperty("data")[0];
            Assert.Equal(fixture.ProjectId, row.GetProperty("project_id").GetGuid());
        }

        var inserted = await client.GetAsync(
            $"/rest/v1/tenant_items?f=nombre.eq.{Uri.EscapeDataString(name)}");
        using (var json = await ReadJsonAsync(inserted))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task OtherProject_CannotReadOrWriteThisProjectData()
    {
        using var admin = fixture.Factory.CreateAdminClient();
        var otherSlug = "it-" + Guid.NewGuid().ToString("N")[..10];
        Guid? otherProjectId = null;
        var cleanup = new ProjectCleanup(
            fixture.Factory.ProvisioningConnectionString,
            fixture.Factory.ControlConnectionString);

        var aEmail = $"a-{Guid.NewGuid():N}@example.test";
        var bEmail = $"b-{Guid.NewGuid():N}@example.test";
        string? bKey = null;

        try
        {
            var create = await admin.PostAsJsonAsync(
                "/api/v1/projects",
                new { slug = otherSlug, name = "Other Project" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            using (var createJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
            {
                otherProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            }

            var keyResponse = await admin.PostAsJsonAsync(
                $"/api/v1/projects/{otherProjectId}/keys",
                new { label = "other" });
            Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);

            using (var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
            {
                bKey = keyJson.RootElement.GetProperty("key").GetString();
            }

            await CreateOtherProjectTableAsync(otherSlug, bEmail);

            using var clientA = fixture.CreateRuntimeClientWithKey();
            var aInsert = await SendJsonAsync(
                clientA,
                HttpMethod.Post,
                "/rest/v1/contactos?select=id",
                new { nombre = "Only A", email = aEmail });
            Assert.Equal(HttpStatusCode.Created, aInsert.StatusCode);

            Guid aId;
            using (var aJson = await ReadJsonAsync(aInsert))
            {
                aId = aJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
            }

            using var clientB = fixture.Factory.CreateClient();
            Assert.NotNull(bKey);
            clientB.DefaultRequestHeaders.Add("apikey", bKey);

            var aReadsB = await clientA.GetAsync(
                $"/rest/v1/contactos?f=email.eq.{Uri.EscapeDataString(bEmail)}");
            using (var json = await ReadJsonAsync(aReadsB))
            {
                Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
            }

            var bReadsB = await clientB.GetAsync(
                $"/rest/v1/contactos?f=email.eq.{Uri.EscapeDataString(bEmail)}");
            using (var json = await ReadJsonAsync(bReadsB))
            {
                Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
                Assert.Equal("B only", json.RootElement.GetProperty("data")[0].GetProperty("nombre").GetString());
            }

            var bReadsA = await clientB.GetAsync(
                $"/rest/v1/contactos?f=email.eq.{Uri.EscapeDataString(aEmail)}");
            using (var json = await ReadJsonAsync(bReadsA))
            {
                Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
            }

            var bReadsInternal = await clientB.GetAsync("/rest/v1/interno");
            Assert.Equal(HttpStatusCode.NotFound, bReadsInternal.StatusCode);

            await clientA.DeleteAsync($"/rest/v1/contactos?f=id.eq.{aId}");
        }
        finally
        {
            if (otherProjectId is not null)
            {
                await cleanup.DropProjectAsync(otherSlug, otherProjectId);
            }
        }
    }

    [Fact]
    public async Task SqlInjectionInValuesAndIdentifiers_DoesNotChangeTheSchema()
    {
        using var client = fixture.CreateRuntimeClientWithKey();
        var email = $"safe-{Guid.NewGuid():N}@example.test";

        var insert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos?select=id",
            new { nombre = "Safe", email });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        Guid id;
        using (var json = await ReadJsonAsync(insert))
        {
            id = json.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
        }

        var injectedFilter = await client.GetAsync(
            $"/rest/v1/contactos?select=email&f=nombre.eq.{Uri.EscapeDataString("' OR 1=1 --")}");
        Assert.Equal(HttpStatusCode.OK, injectedFilter.StatusCode);
        using (var json = await ReadJsonAsync(injectedFilter))
        {
            Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        }

        var injectedSelect = await client.GetAsync(
            $"/rest/v1/contactos?select={Uri.EscapeDataString("id\"; DROP TABLE contactos; --")}");
        Assert.Equal(HttpStatusCode.BadRequest, injectedSelect.StatusCode);
        Assert.Equal("INVALID_COLUMN", await ErrorCodeAsync(injectedSelect));

        var injectedTable = await client.GetAsync(
            $"/rest/v1/{Uri.EscapeDataString("contactos; DROP TABLE contactos; --")}");
        Assert.Equal(HttpStatusCode.NotFound, injectedTable.StatusCode);

        var injectedValue = "'); DROP TABLE contactos; --";
        var injectedInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos?select=id,nombre",
            new { nombre = injectedValue, email = $"inject-{Guid.NewGuid():N}@example.test" });
        Assert.Equal(HttpStatusCode.Created, injectedInsert.StatusCode);

        using (var json = await ReadJsonAsync(injectedInsert))
        {
            Assert.Equal(injectedValue, json.RootElement.GetProperty("data")[0].GetProperty("nombre").GetString());
            var injectedId = json.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
            await client.DeleteAsync($"/rest/v1/contactos?f=id.eq.{injectedId}");
        }

        // The original table is intact and still serves the safe row.
        var stillThere = await client.GetAsync($"/rest/v1/contactos?f=id.eq.{id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        using (var json = await ReadJsonAsync(stillThere))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        }

        await client.DeleteAsync($"/rest/v1/contactos?f=id.eq.{id}");
    }

    [Fact]
    public async Task PayloadLimit_RejectsOversizedBodies()
    {
        using var client = fixture.CreateRuntimeClientWithKey();

        var payload = "{\"nombre\":\"" + new string('a', 1_100_000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/contactos")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("PAYLOAD_TOO_LARGE", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task PooledConnections_DoNotLeakRoleOrClaims()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IRuntimeProjectResolver>();
        var connections = scope.ServiceProvider.GetRequiredService<RuntimeConnectionFactory>();

        var resolved = await resolver.ResolveAsync(fixture.ApiKey);
        Assert.Equal(RuntimeResolveStatus.Success, resolved.Status);
        var context = resolved.Context!;

        await using (var connection = await connections.OpenAsync(context))
        {
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var role = new NpgsqlCommand("SELECT set_config('role', @role, true)", connection, transaction))
            {
                role.Parameters.AddWithValue("role", context.AnonymousRole);
                await role.ExecuteNonQueryAsync();
            }

            await using (var claims = new NpgsqlCommand(
                             "SELECT set_config('domibase.project_id', @project, true)", connection, transaction))
            {
                claims.Parameters.AddWithValue("project", context.ProjectId.ToString());
                await claims.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await using (var connection = await connections.OpenAsync(context))
        {
            await using var user = new NpgsqlCommand("SELECT current_user", connection);
            Assert.Equal(context.RuntimeUsername, (string)(await user.ExecuteScalarAsync())!);

            await using var setting = new NpgsqlCommand(
                "SELECT current_setting('domibase.project_id', true)", connection);
            var leaked = await setting.ExecuteScalarAsync();
            var leakedValue = leaked is null or DBNull ? null : leaked as string;
            Assert.True(
                string.IsNullOrEmpty(leakedValue),
                $"Leaked runtime claim after the transaction: {leakedValue}");
        }
    }

    [Fact]
    public async Task RateLimiter_ReturnsCanonicalEnvelope()
    {
        var previousPermits = Environment.GetEnvironmentVariable("RUNTIME_RATE_LIMIT_PERMITS");
        var previousWindow = Environment.GetEnvironmentVariable("RUNTIME_RATE_LIMIT_WINDOW_SECONDS");

        Environment.SetEnvironmentVariable("RUNTIME_RATE_LIMIT_PERMITS", "1");
        Environment.SetEnvironmentVariable("RUNTIME_RATE_LIMIT_WINDOW_SECONDS", "60");

        try
        {
            using var factory = new DomiBaseWebApplicationFactory();
            using var client = factory.CreateClient();

            var first = await client.GetAsync("/rest/v1/contactos");
            Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

            var second = await client.GetAsync("/rest/v1/contactos");
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.Equal("RATE_LIMITED", await ErrorCodeAsync(second));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNTIME_RATE_LIMIT_PERMITS", previousPermits);
            Environment.SetEnvironmentVariable("RUNTIME_RATE_LIMIT_WINDOW_SECONDS", previousWindow);
        }
    }

    private async Task<HttpResponseMessage> SendWithKeyAsync(string apiKey)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/v1/contactos");
        request.Headers.Add("apikey", apiKey);
        return await client.SendAsync(request);
    }

    private async Task CreateOtherProjectTableAsync(string slug, string email)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.Factory.ProvisioningConnectionString)
        {
            Database = ProjectNaming.ToDatabaseName(slug)
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        var owner = Quote(ProjectNaming.ToOwnerRole(slug));
        var anon = Quote(ProjectNaming.ToAnonymousRole(slug));

        await ExecuteAsync(connection, $"SET ROLE {owner}");
        await ExecuteAsync(
            connection,
            $"""
             CREATE TABLE public.contactos (
                 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                 nombre text NOT NULL,
                 email text NOT NULL
             );
             ALTER TABLE public.contactos ENABLE ROW LEVEL SECURITY;
             GRANT SELECT ON public.contactos TO {anon};
             CREATE POLICY contactos_select ON public.contactos FOR SELECT TO {anon} USING (true);
             INSERT INTO public.contactos (nombre, email) VALUES ('B only', '{email.Replace("'", "''")}');
             """);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object body)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body)
        };

        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
