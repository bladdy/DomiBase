using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace DomiBase.IntegrationTests;

public class ApiKeyLifecycleTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    private const string RuntimeTable = "/rest/v1/contactos";

    [Fact]
    public async Task SecretKey_CanBeIssuedButRuntimeAlwaysRejectsIt()
    {
        var slug = UniqueSlug();
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var admin = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(admin, slug);

            var secret = await CreateKeyAsync(admin, projectId, new { kind = "secret", label = "server" });
            Assert.Equal("Secret", secret.RootElement.GetProperty("kind").GetString());
            Assert.StartsWith("db_secret_", secret.RootElement.GetProperty("key").GetString());
            Assert.DoesNotContain("secretHash", secret.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);

            using var secretClient = RuntimeClient(secret.RootElement.GetProperty("key").GetString()!);
            Assert.Equal(HttpStatusCode.Unauthorized, (await secretClient.GetAsync(RuntimeTable)).StatusCode);

            var rotatedSecret = await RotateAsync(admin, projectId, secret.RootElement.GetProperty("id").GetGuid(), 0);
            Assert.Equal(HttpStatusCode.Created, rotatedSecret.StatusCode);
            using var rotatedJson = JsonDocument.Parse(await rotatedSecret.Content.ReadAsStringAsync());
            Assert.StartsWith(
                "db_secret_",
                rotatedJson.RootElement.GetProperty("key").GetString());

            using var rotatedClient = RuntimeClient(rotatedJson.RootElement.GetProperty("key").GetString()!);
            Assert.Equal(HttpStatusCode.Unauthorized, (await rotatedClient.GetAsync(RuntimeTable)).StatusCode);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    [Fact]
    public async Task Rotation_KeepsOldKeyValidDuringOverlapAndRejectsItAfterwards()
    {
        var slug = UniqueSlug();
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var admin = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(admin, slug);

            var original = await CreateKeyAsync(admin, projectId, new { label = "original" });
            var originalId = original.RootElement.GetProperty("id").GetGuid();
            var originalValue = original.RootElement.GetProperty("key").GetString()!;

            using var originalClient = RuntimeClient(originalValue);
            Assert.False((await originalClient.GetAsync(RuntimeTable)).StatusCode == HttpStatusCode.Unauthorized);

            var rotation = await RotateAsync(admin, projectId, originalId, 60);
            Assert.Equal(HttpStatusCode.Created, rotation.StatusCode);
            using var rotationJson = JsonDocument.Parse(await rotation.Content.ReadAsStringAsync());
            var newValue = rotationJson.RootElement.GetProperty("key").GetString()!;
            var newId = rotationJson.RootElement.GetProperty("id").GetGuid();

            Assert.False(originalId == newId);
            Assert.StartsWith("db_public_", newValue);
            Assert.False(string.Equals(originalValue, newValue, StringComparison.Ordinal));

            // Both values resolve while the overlap is open.
            using var overlapClient = RuntimeClient(originalValue);
            Assert.False((await overlapClient.GetAsync(RuntimeTable)).StatusCode == HttpStatusCode.Unauthorized);
            using var newClient = RuntimeClient(newValue);
            Assert.False((await newClient.GetAsync(RuntimeTable)).StatusCode == HttpStatusCode.Unauthorized);

            var overlapExpires = AsDateTimeOffset(await ReadAsync(
                "SELECT previous_hash_expires_at FROM api_keys WHERE id = @id",
                newId));
            Assert.NotNull(overlapExpires);
            Assert.True(overlapExpires > DateTimeOffset.UtcNow);

            await ExecuteControlAsync(
                "UPDATE api_keys SET previous_hash_expires_at = now() - interval '1 minute' WHERE id = @id",
                newId);

            using var expiredClient = RuntimeClient(originalValue);
            Assert.Equal(HttpStatusCode.Unauthorized, (await expiredClient.GetAsync(RuntimeTable)).StatusCode);
            using var stillValidClient = RuntimeClient(newValue);
            Assert.False((await stillValidClient.GetAsync(RuntimeTable)).StatusCode == HttpStatusCode.Unauthorized);

            // The old row is revoked, so deleting it again is a no-op rather than a new revocation.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM api_keys WHERE id = @id AND revoked_at IS NOT NULL",
                originalId));
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM audit_entries WHERE target_id = @id::text AND action = 'api_key.rotate'",
                newId));

            using var keysResponse = await admin.GetAsync($"/api/v1/projects/{projectId}/keys");
            var keysBody = await keysResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(originalValue, keysBody);
            Assert.DoesNotContain(newValue, keysBody);
            Assert.DoesNotContain("previousSecretHash", keysBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    [Fact]
    public async Task Rotation_WithZeroOverlap_RevokesOldValueImmediately()
    {
        var slug = UniqueSlug();
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var admin = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(admin, slug);
            var original = await CreateKeyAsync(admin, projectId, new { label = "zero-overlap" });
            var originalValue = original.RootElement.GetProperty("key").GetString()!;

            var rotation = await RotateAsync(
                admin,
                projectId,
                original.RootElement.GetProperty("id").GetGuid(),
                0);
            Assert.Equal(HttpStatusCode.Created, rotation.StatusCode);
            using var rotationJson = JsonDocument.Parse(await rotation.Content.ReadAsStringAsync());
            var newId = rotationJson.RootElement.GetProperty("id").GetGuid();

            Assert.Null(AsDateTimeOffset(await ReadAsync(
                "SELECT previous_hash_expires_at FROM api_keys WHERE id = @id",
                newId)));
            using var oldClient = RuntimeClient(originalValue);
            Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync(RuntimeTable)).StatusCode);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    [Fact]
    public async Task ExpiredKey_IsRejectedByRuntime()
    {
        var slug = UniqueSlug();
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var admin = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(admin, slug);
            var created = await CreateKeyAsync(
                admin,
                projectId,
                new { expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            var keyId = created.RootElement.GetProperty("id").GetGuid();
            var value = created.RootElement.GetProperty("key").GetString()!;

            Assert.NotNull(AsDateTimeOffset(await ReadAsync(
                "SELECT expires_at FROM api_keys WHERE id = @id",
                keyId)));

            await ExecuteControlAsync(
                "UPDATE api_keys SET expires_at = now() - interval '1 minute' WHERE id = @id",
                keyId);

            using var client = RuntimeClient(value);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(RuntimeTable)).StatusCode);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    [Fact]
    public async Task Rotation_RejectsInvalidOverlapMissingKeyAndRevokedKey()
    {
        var slug = UniqueSlug();
        Guid projectId = Guid.Empty;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var admin = factory.CreateAdminClient();
            projectId = await CreateProjectAsync(admin, slug);
            var created = await CreateKeyAsync(admin, projectId, new { label = "validation" });
            var keyId = created.RootElement.GetProperty("id").GetGuid();

            var tooLong = await RotateAsync(admin, projectId, keyId, 1441);
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

            var missing = await RotateAsync(admin, projectId, Guid.NewGuid(), 5);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await admin.DeleteAsync($"/api/v1/projects/{projectId}/keys/{keyId}")).StatusCode);

            var revoked = await RotateAsync(admin, projectId, keyId, 5);
            Assert.Equal(HttpStatusCode.Conflict, revoked.StatusCode);

            var badExpiry = await admin.PostAsJsonAsync(
                $"/api/v1/projects/{projectId}/keys",
                new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            Assert.Equal(HttpStatusCode.BadRequest, badExpiry.StatusCode);
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId == Guid.Empty ? null : projectId);
        }
    }

    private HttpClient RuntimeClient(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", key);
        return client;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient admin, string slug)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "API key lifecycle test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> CreateKeyAsync(HttpClient admin, Guid projectId, object body)
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/projects/{projectId}/keys", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> RotateAsync(
        HttpClient admin,
        Guid projectId,
        Guid keyId,
        int overlapMinutes) =>
        admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/keys/{keyId}/rotate",
            new { overlapMinutes });

    private async Task<object?> ReadAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static DateTimeOffset? AsDateTimeOffset(object? value) => value switch
    {
        null => null,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        _ => throw new InvalidOperationException($"Unexpected timestamp type: {value.GetType().Name}.")
    };

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteControlAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static string UniqueSlug() => "keys-" + Guid.NewGuid().ToString("N")[..10];
}
