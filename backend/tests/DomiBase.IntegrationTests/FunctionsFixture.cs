using System.Net.Http.Json;
using System.Text.Json;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class FunctionsCollection : ICollectionFixture<FunctionsFixture>
{
    public const string Name = "Functions";
}

public sealed class FunctionsFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Slug = "funciones-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();
        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Functions Integration Test" });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using (var json = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = json.RootElement.GetProperty("id").GetGuid();
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "functions-integration" });
        Assert.Equal(System.Net.HttpStatusCode.Created, keyResponse.StatusCode);

        using (var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
        {
            ApiKey = keyJson.RootElement.GetProperty("key").GetString()!;
        }
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

    public async Task<Guid> CreateFunctionAsync(
        string name,
        string wat,
        object? limits = null,
        System.Net.HttpStatusCode expected = System.Net.HttpStatusCode.Created)
    {
        using var admin = Factory.CreateAdminClient();
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["moduleBase64"] = TestWasmModules.Base64(wat)
        };
        if (limits is not null)
        {
            body["limits"] = limits;
        }

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/functions",
            body);
        Assert.Equal(expected, response.StatusCode);

        if (expected != System.Net.HttpStatusCode.Created)
        {
            return Guid.Empty;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<string> RegisterUserAsync()
    {
        using var client = CreateRuntimeClient();
        var response = await client.PostAsJsonAsync(
            "/auth/v1/register",
            new { email = $"{Guid.NewGuid():N}@example.com", password = "secure-password-123" });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    public HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = CreateRuntimeClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task<(string ApiKey, Guid ProjectId)> CreateSecondProjectAsync()
    {
        var slug = "funciones2-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Functions Second Project" });
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);

        Guid projectId;
        using (var json = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            projectId = json.RootElement.GetProperty("id").GetGuid();
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/keys",
            new { label = "functions-second" });
        Assert.Equal(System.Net.HttpStatusCode.Created, keyResponse.StatusCode);

        using var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
        return (keyJson.RootElement.GetProperty("key").GetString()!, projectId);
    }

    public async Task CleanupSecondProjectAsync(Guid projectId)
    {
        using var admin = Factory.CreateAdminClient();
        var response = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}");
        var slug = response.GetProperty("slug").GetString()!;
        await new ProjectCleanup(Factory.ProvisioningConnectionString, Factory.ControlConnectionString)
            .DropProjectAsync(slug, projectId);
    }
}
