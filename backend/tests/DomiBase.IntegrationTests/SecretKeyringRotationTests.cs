using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DomiBase.Application.ApiKeys;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Verifies that rotating both deployment secrets keeps already issued keys and
/// already encrypted runtime credentials usable, while new keys use the new pepper.
/// </summary>
public class SecretKeyringRotationTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task RotatedPepperAndAesKey_KeepExistingKeysAndCredentialsWorking()
    {
        var slug = "kr-" + Guid.NewGuid().ToString("N")[..10];
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        var newPepper = "rotated-pepper-" + Guid.NewGuid().ToString("N");
        var newAesKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Build the original host before overriding process-wide environment variables so
        // its singleton generator keeps the pre-rotation pepper.
        var originalGenerator = factory.Services.GetRequiredService<IApiKeyGenerator>();

        using var rotatedFactory = new KeyringWebApplicationFactory(
            factory.ApiKeyPepper,
            factory.SecretsKey,
            newPepper,
            newAesKey);

        try
        {
            using var originalAdmin = factory.CreateAdminClient();
            var created = await originalAdmin.PostAsJsonAsync(
                "/api/v1/projects",
                new { slug, name = "Keyring rotation test" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            projectId = createdJson.RootElement.GetProperty("id").GetGuid();

            var keyResponse = await originalAdmin.PostAsJsonAsync(
                $"/api/v1/projects/{projectId}/keys",
                new { label = "before-rotation" });
            Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);
            using var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            var legacyKey = keyJson.RootElement.GetProperty("key").GetString()!;
            var legacyKeyId = keyJson.RootElement.GetProperty("id").GetGuid();

            // The legacy key still resolves after the host restarts with a rotated pepper
            // and a rotated AES key: the old v1 ciphertext is decrypted with the legacy key.
            using var rotatedClient = RuntimeClient(rotatedFactory, legacyKey);
            Assert.False((await rotatedClient.GetAsync("/rest/v1/contactos")).StatusCode ==
                         HttpStatusCode.Unauthorized);

            using var rotatedAdmin = rotatedFactory.CreateAdminClient();
            var freshResponse = await rotatedAdmin.PostAsJsonAsync(
                $"/api/v1/projects/{projectId}/keys",
                new { label = "after-rotation" });
            Assert.Equal(HttpStatusCode.Created, freshResponse.StatusCode);
            using var freshJson = JsonDocument.Parse(await freshResponse.Content.ReadAsStringAsync());
            var freshKey = freshJson.RootElement.GetProperty("key").GetString()!;
            var freshKeyId = freshJson.RootElement.GetProperty("id").GetGuid();

            using var freshClient = RuntimeClient(rotatedFactory, freshKey);
            Assert.False((await freshClient.GetAsync("/rest/v1/contactos")).StatusCode ==
                         HttpStatusCode.Unauthorized);

            var legacyHash = await ReadHashAsync(legacyKeyId);
            var freshHash = await ReadHashAsync(freshKeyId);

            var rotatedGenerator = rotatedFactory.Services.GetRequiredService<IApiKeyGenerator>();

            Assert.True(originalGenerator.Verify(legacyKey, legacyHash));
            Assert.True(rotatedGenerator.Verify(legacyKey, legacyHash));
            Assert.True(rotatedGenerator.Verify(freshKey, freshHash));
            Assert.False(originalGenerator.Verify(freshKey, freshHash));
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    private static HttpClient RuntimeClient(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", key);
        return client;
    }

    private async Task<string> ReadHashAsync(Guid keyId)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT secret_hash FROM api_keys WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", keyId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public sealed class KeyringWebApplicationFactory(
        string legacyPepper,
        string legacyAesKey,
        string newPepper,
        string newAesKey) : DomiBaseWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["DOMIBASE_API_KEY_PEPPERS"] = $"{newPepper},{legacyPepper}",
                ["DOMIBASE_SECRETS_KEYS"] = $"rotated:{newAesKey},legacy:{legacyAesKey}"
            })
    {
    }
}
