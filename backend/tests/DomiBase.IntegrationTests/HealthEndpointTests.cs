using System.Net;
using System.Text.Json;

namespace DomiBase.IntegrationTests;

public class HealthEndpointTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task Health_ReturnsOkWithServiceIdentity()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("domibase-api", json.RootElement.GetProperty("service").GetString());
    }
}
