using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;
using DomiBase.Domain.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class AuthRepositoryCollection : ICollectionFixture<AuthRepositoryFixture>
{
    public const string Name = "AuthRepository";
}

/// <summary>
/// Creates one real project with the auth schema provisioned by <see cref="IProjectProvisioner"/>.
/// Auth repository tests share it so provisioning happens once.
/// </summary>
public sealed class AuthRepositoryFixture : IAsyncLifetime
{
    public DomiBaseWebApplicationFactory Factory { get; } = new();

    public string Slug { get; private set; } = string.Empty;

    public Guid ProjectId { get; private set; }

    public string ApiKey { get; private set; } = string.Empty;

    public RuntimeProjectContext RuntimeContext { get; private set; } = null!;

    public IAuthUserRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Slug = "auth-it-" + Guid.NewGuid().ToString("N")[..8];

        using var admin = Factory.CreateAdminClient();

        var createResponse = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug = Slug, name = "Auth Repository Integration Test" });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using (var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            ProjectId = createJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Ready", createJson.RootElement.GetProperty("status").GetString());
        }

        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{ProjectId}/keys",
            new { label = "auth-repository-integration" });
        Assert.Equal(System.Net.HttpStatusCode.Created, keyResponse.StatusCode);

        using (var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
        {
            ApiKey = keyJson.RootElement.GetProperty("key").GetString()!;
        }

        using var scope = Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IRuntimeProjectResolver>();
        var resolved = await resolver.ResolveAsync(ApiKey);
        Assert.Equal(RuntimeResolveStatus.Success, resolved.Status);

        RuntimeContext = resolved.Context!;
        Repository = scope.ServiceProvider.GetRequiredService<IAuthUserRepository>();
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
}
