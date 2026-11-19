using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Domain.Projects;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class StorageCollection : ICollectionFixture<StorageFixture>
{
    public const string Name = "Storage";
}

public sealed class StorageFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public string DatabaseName => ProjectNaming.ToDatabaseName(Slug);

    public async Task InitializeAsync()
    {
        Slug = "storage-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();

        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Storage Integration Test" });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "storage-integration" });
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

    public async Task<Guid> CreateBucketAsync(string name, bool publicAccess = true)
    {
        using var admin = Factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/buckets",
            new { name, publicAccess });

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Registers a user in this project and returns their access token.</summary>
    public async Task<string> RegisterUserAsync()
    {
        using var client = CreateRuntimeClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync(
            "/auth/v1/register",
            new { email, password = "secure-password-123" });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;
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
        var slug = "storage2-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = Factory.CreateAdminClient();

        var create = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Storage Second Project" });
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);

        Guid projectId;
        using (var json = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            projectId = json.RootElement.GetProperty("id").GetGuid();
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/keys",
            new { label = "storage-second" });
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
