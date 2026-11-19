using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace DomiBase.IntegrationTests;

public class ProjectRateLimitTests
{
    private const int DefaultPermits = 100;
    private const int DefaultWindowSeconds = 60;

    [Fact]
    public async Task PerProjectLimits_AreIndependentAndSharedAcrossKeys()
    {
        using var factory = new RateLimitFactory(DefaultPermits, DefaultWindowSeconds);
        using var admin = factory.CreateAdminClient();

        var first = await CreateProjectAsync(factory, admin, "rl-a");
        var second = await CreateProjectAsync(factory, admin, "rl-b");

        try
        {
            await PatchAsync(admin, first.Id, 2, 60);
            await PatchAsync(admin, second.Id, 5, 60);

            var firstKeyA = await CreateKeyAsync(admin, first.Id);
            var firstKeyB = await CreateKeyAsync(admin, first.Id);
            var secondKey = await CreateKeyAsync(admin, second.Id);

            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(factory, firstKeyA)).StatusCode);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(factory, firstKeyA)).StatusCode);

            var limited = await SendAsync(factory, firstKeyA);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal("RATE_LIMITED", await ErrorCodeAsync(limited));

            // A different key of the same project shares the project partition.
            Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(factory, firstKeyB)).StatusCode);

            for (var i = 0; i < 5; i++)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(factory, secondKey)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(factory, secondKey)).StatusCode);

            var body = await limited.Content.ReadAsStringAsync();
            Assert.DoesNotContain(firstKeyA, body, StringComparison.Ordinal);
            Assert.DoesNotContain(first.Id.ToString(), body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(factory, first, second);
        }
    }

    [Fact]
    public async Task InvalidKeys_FallBackToAddressPartition()
    {
        using var factory = new RateLimitFactory(1, 60);
        using var client = factory.CreateClient();

        var first = await SendAsync(factory, "db_public_invalid-key");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        var second = await SendAsync(factory, "db_public_invalid-key");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("RATE_LIMITED", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task RateLimitConfiguration_IsAdminOnlyValidatedAndAudited()
    {
        using var factory = new RateLimitFactory(DefaultPermits, DefaultWindowSeconds);
        using var admin = factory.CreateAdminClient();
        using var anonymous = factory.CreateClient();

        var project = await CreateProjectAsync(factory, admin, "rl-config");

        try
        {
            var noToken = await anonymous.PatchAsJsonAsync(
                $"/api/v1/projects/{project.Id}/rate-limit",
                new { permits = 2, windowSeconds = 60 });
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

            using var publicKeyOnly = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/projects/{project.Id}/rate-limit")
            {
                Content = JsonContent.Create(new { permits = 2, windowSeconds = 60 })
            };
            publicKeyOnly.Headers.Add("apikey", "db_public_invalid-key");
            using var withPublicKey = await anonymous.SendAsync(publicKeyOnly);
            Assert.Equal(HttpStatusCode.Unauthorized, withPublicKey.StatusCode);

            var invalidRange = await PatchAsync(admin, project.Id, 0, 60);
            Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);
            Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(invalidRange));

            using var cleared = JsonDocument.Parse(
                await (await PatchAsync(admin, project.Id, null, null)).Content.ReadAsStringAsync());
            Assert.Equal(DefaultPermits, cleared.RootElement.GetProperty("effectiveRateLimitPermits").GetInt32());
            Assert.Equal(DefaultWindowSeconds, cleared.RootElement.GetProperty("effectiveRateLimitWindowSeconds").GetInt32());

            using var overridden = JsonDocument.Parse(
                await (await PatchAsync(admin, project.Id, 7, 90)).Content.ReadAsStringAsync());
            Assert.Equal(7, overridden.RootElement.GetProperty("rateLimitPermits").GetInt32());
            Assert.Equal(90, overridden.RootElement.GetProperty("rateLimitWindowSeconds").GetInt32());
            Assert.Equal(7, overridden.RootElement.GetProperty("effectiveRateLimitPermits").GetInt32());

            await AssertAuditAsync(factory, project.Id);
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    private static async Task<ProjectInfo> CreateProjectAsync(
        DomiBaseWebApplicationFactory factory,
        HttpClient admin,
        string prefix)
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}"[..36];
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Rate limit integration test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ProjectInfo(slug, json.RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<string> CreateKeyAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/keys",
            new { label = "rate-limit-test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("key").GetString()!;
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient admin,
        Guid projectId,
        int? permits,
        int? windowSeconds) =>
        admin.PatchAsJsonAsync(
            $"/api/v1/projects/{projectId}/rate-limit",
            new { permits, windowSeconds });

    private static async Task<HttpResponseMessage> SendAsync(
        DomiBaseWebApplicationFactory factory,
        string apiKey)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/v1/contactos");
        request.Headers.Add("apikey", apiKey);
        return await client.SendAsync(request);
    }

    private static async Task AssertAuditAsync(DomiBaseWebApplicationFactory factory, Guid projectId)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM audit_entries
            WHERE action = 'project.rate_limit.updated' AND target_id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", projectId.ToString());

        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 2);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task CleanupAsync(
        DomiBaseWebApplicationFactory factory,
        params ProjectInfo[] projects)
    {
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        foreach (var project in projects)
        {
            await cleanup.DropProjectAsync(project.Slug, project.Id);
        }
    }

    private sealed record ProjectInfo(string Slug, Guid Id);

    private sealed class RateLimitFactory(int permits, int windowSeconds) : DomiBaseWebApplicationFactory(
        new Dictionary<string, string?>
        {
            ["RUNTIME_RATE_LIMIT_PERMITS"] = permits.ToString(),
            ["RUNTIME_RATE_LIMIT_WINDOW_SECONDS"] = windowSeconds.ToString()
        });
}
