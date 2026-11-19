using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DomiBase.SecurityTests;

/// <summary>
/// Verifies that the Control Plane is not reachable without the administrative
/// token and that the Runtime Plane never exposes administrative routes.
/// </summary>
public class ControlPlaneAuthenticationTests(SecurityWebApplicationFactory factory)
    : IClassFixture<SecurityWebApplicationFactory>
{
    [Fact]
    public async Task ControlEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ControlEndpoint_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ControlEndpoint_WithValidToken_ReachesValidationLayer()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-DomiBase-Admin-Token", SecurityWebApplicationFactory.AdminToken);

        var response = await client.SendAsync(request);

        // Authentication succeeded and the request reached application validation.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ControlEndpoint_AcceptsBearerToken()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecurityWebApplicationFactory.AdminToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/tables");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaEndpoint_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/functions");
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaEndpoint_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/views");
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaMutation_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/tables")
        {
            Content = JsonContent.Create(new { name = "contactos", columns = new[] { new { name = "id", type = "int4" } } })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaMutation_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/tables/contactos/policies")
        {
            Content = JsonContent.Create(new
            {
                name = "publico",
                commands = new[] { "SELECT" },
                roles = new[] { "anon" },
                @using = "true",
                withCheck = "true"
            })
        };
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SchemaDrop_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/schema/tables/contactos/columns/id");
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task MigrationEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/migrations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task MigrationEndpoint_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/migrations");
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task MigrationMutation_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/migrations")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                name = "initial",
                operations = new object[]
                {
                    new { type = "create_table", name = "contactos", columns = new[] { new { name = "id", type = "int4" } } }
                }
            })
        };
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SqlQueryEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/sql/query")
        {
            Content = JsonContent.Create(new { sql = "SELECT 1" })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SqlQueryEndpoint_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/sql/query")
        {
            Content = JsonContent.Create(new { sql = "SELECT 1" })
        };
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SqlQueryEndpoint_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/sql/query")
        {
            Content = JsonContent.Create(new { sql = "SELECT 1" })
        };
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DataPreviewEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/data/tables/contactos/rows");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DataPreviewEndpoint_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/data/tables/contactos/rows");
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DataPreviewEndpoint_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/data/tables/contactos/rows");
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ExtensionCatalog_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/extensions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ExtensionInstall_WithWrongToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/extensions")
        {
            Content = JsonContent.Create(new { name = "pgcrypto" })
        };
        request.Headers.Add("X-DomiBase-Admin-Token", "definitely-not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ExtensionUninstall_WithOnlyPublicApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/extensions/pgcrypto");
        request.Headers.Add("apikey", "db_public_not_an_admin_token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task RuntimePlane_IsSealedButAvailableWithoutAdminToken()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/rest/v1/contactos");

        // No administrative token is required, but a project API key is: the Runtime
        // never falls back to Control Plane authentication.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("MISSING_API_KEY", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task RuntimePlane_NeverExposesControlRoutes()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/rest/v1/projects");
        request.Headers.Add("X-DomiBase-Admin-Token", SecurityWebApplicationFactory.AdminToken);

        var response = await client.SendAsync(request);

        // The administrative token has no meaning under /rest/v1: the request is treated
        // as a Runtime operation and is rejected before any table or admin action is reached.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("MISSING_API_KEY", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task RotateKey_WithoutAdminToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111/keys/22222222-2222-2222-2222-222222222222/rotate")
        {
            Content = JsonContent.Create(new { overlapMinutes = 60 })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DeleteProject_WithoutAdminToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/v1/projects/11111111-1111-1111-1111-111111111111")
        {
            Content = JsonContent.Create(new { confirmSlug = "cualquier-proyecto" })
        };
        request.Headers.Add("X-DomiBase-Confirm-Delete", "delete");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AdminLogin_WithValidCredentials_IssuesSessionAcceptedByControlPlane()
    {
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/admin/v1/login", new
        {
            username = SecurityWebApplicationFactory.AdminUsername,
            password = SecurityWebApplicationFactory.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var sessionToken = loginJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionToken));
        Assert.NotEqual(SecurityWebApplicationFactory.AdminToken, sessionToken);
        Assert.True(loginJson.RootElement.TryGetProperty("expiresAt", out _));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-DomiBase-Admin-Token", sessionToken);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AdminLogin_WithWrongPassword_ReturnsInvalidCredentials()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/v1/login", new
        {
            username = SecurityWebApplicationFactory.AdminUsername,
            password = "not-the-owner-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ReadErrorCodeAsync(response));
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
