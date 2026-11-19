using System.Net;
using System.Text.Json;

namespace DomiBase.SecurityTests;

/// <summary>
/// Audit reads are Control Plane only. A public Runtime key must never reveal administrative
/// history, and no response may echo configured secrets or raw diagnostics.
/// </summary>
public class AuditSecurityTests(SecurityWebApplicationFactory factory)
    : IClassFixture<SecurityWebApplicationFactory>
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    public static TheoryData<string> ControlRoutes => new()
    {
        "/api/v1/audit",
        $"/api/v1/projects/{ProjectId}/provisioning-operations"
    };

    [Theory]
    [MemberData(nameof(ControlRoutes))]
    public async Task AuditRoute_WithoutAdminToken_ReturnsUnauthorized(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ErrorCodeAsync(response));
    }

    [Theory]
    [MemberData(nameof(ControlRoutes))]
    public async Task AuditRoute_WithOnlyPublicApiKey_ReturnsUnauthorized(string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("apikey", "db_public_security_test_key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task AuditReads_AreNotExposedUnderTheRuntimePrefix()
    {
        using var client = CreateAdminClient();

        using var response = await client.GetAsync("/rest/v1/audit");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("audit_entries", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provisioning_operations", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditResponses_DoNotContainConfiguredSecrets()
    {
        using var client = CreateAdminClient();

        using var audit = await client.GetAsync("/api/v1/audit");
        using var operations = await client.GetAsync($"/api/v1/projects/{ProjectId}/provisioning-operations");
        var body = await audit.Content.ReadAsStringAsync() + await operations.Content.ReadAsStringAsync();

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
