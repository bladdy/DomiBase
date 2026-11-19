using System.Net;
using System.Text.Json;

namespace DomiBase.SecurityTests;

/// <summary>
/// Backup administration must remain exclusively in the Control Plane: public keys never
/// grant access, runtime routes never expose it, and errors never echo configured secrets.
/// </summary>
public class BackupSecurityTests(SecurityWebApplicationFactory factory)
    : IClassFixture<SecurityWebApplicationFactory>
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid BackupId = Guid.Parse("22222222-2222-7222-8222-222222222222");

    public static TheoryData<string, HttpMethod> ControlRoutes => new()
    {
        { $"/api/v1/projects/{ProjectId}/backups", HttpMethod.Get },
        { $"/api/v1/projects/{ProjectId}/backups", HttpMethod.Post },
        { $"/api/v1/projects/{ProjectId}/backups/{BackupId}/download", HttpMethod.Get },
        { $"/api/v1/projects/{ProjectId}/backups/{BackupId}", HttpMethod.Delete },
        { $"/api/v1/projects/{ProjectId}/backups/{BackupId}/restore", HttpMethod.Post }
    };

    [Theory]
    [MemberData(nameof(ControlRoutes))]
    public async Task BackupRoute_WithoutAdminToken_ReturnsUnauthorized(string path, HttpMethod method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ErrorCodeAsync(response));
    }

    [Theory]
    [MemberData(nameof(ControlRoutes))]
    public async Task BackupRoute_WithOnlyPublicApiKey_ReturnsUnauthorized(string path, HttpMethod method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", "db_public_security_test_key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task BackupAdministration_IsNotExposedUnderTheRuntimePrefix()
    {
        using var client = CreateAdminClient();

        using var response = await client.PostAsync($"/rest/v1/projects/{ProjectId}/backups", null);

        Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("backup", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupErrors_DoNotContainConfiguredSecrets()
    {
        using var client = CreateAdminClient();

        using var response = await client.GetAsync($"/api/v1/projects/{ProjectId}/backups");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(SecurityWebApplicationFactory.AdminToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("security-test-pepper-0123456789", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PGPASSWORD", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack", body, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateAdminClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-DomiBase-Admin-Token", SecurityWebApplicationFactory.AdminToken);
        return client;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
