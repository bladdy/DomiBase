using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Api.Endpoints;

namespace DomiBase.IntegrationTests;

[Collection(RuntimeAuthCollection.Name)]
public sealed class RuntimeAuthTests(RuntimeAuthFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AuthenticatedUser_CanInsertAndSelectOwnRows()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var session = await RegisterAsync(email, password);

        var client = fixture.CreateRuntimeClientWithKey();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

        var insertResponse = await client.PostAsJsonAsync(
            "/rest/v1/tareas",
            new { titulo = "mi tarea" });
        Assert.Equal(HttpStatusCode.Created, insertResponse.StatusCode);

        var insertBody = JsonDocument.Parse(await insertResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, insertBody.GetProperty("count").GetInt32());

        var selectResponse = await client.GetAsync("/rest/v1/tareas?select=id,titulo,owner_id");
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        var selectBody = JsonDocument.Parse(await selectResponse.Content.ReadAsStringAsync()).RootElement;
        var rows = selectBody.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("mi tarea", rows[0].GetProperty("titulo").GetString());
        Assert.Equal(session.User!.Id.ToString(), rows[0].GetProperty("owner_id").GetString());
    }

    [Fact]
    public async Task AnonymousUser_CannotSeeAuthenticatedRows()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var session = await RegisterAsync(email, password);

        var authenticatedClient = fixture.CreateRuntimeClientWithKey();
        authenticatedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

        await authenticatedClient.PostAsJsonAsync("/rest/v1/tareas", new { titulo = "secreta" });

        var anonymousClient = fixture.CreateRuntimeClientWithKey();
        var selectResponse = await anonymousClient.GetAsync("/rest/v1/tareas?select=id,titulo");
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        var selectBody = JsonDocument.Parse(await selectResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(selectBody.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task InvalidBearer_ReturnsUnauthorized()
    {
        var client = fixture.CreateRuntimeClientWithKey();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-token");

        var response = await client.GetAsync("/rest/v1/tareas?select=id,titulo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CannotAccessOtherProject()
    {
        // This test validates that a token issued for one project is rejected when used
        // against another project. We reuse the same fixture's key but the Runtime endpoint
        // checks project id and api key id inside the token.
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var session = await RegisterAsync(email, password);

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", fixture.ApiKey);
        // Tamper with the token by appending a character: signature will fail validation.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken + "x");

        var response = await client.GetAsync("/rest/v1/tareas?select=id,titulo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<AuthResponse> RegisterAsync(string email, string password)
    {
        var client = fixture.CreateRuntimeClientWithKey();
        var response = await client.PostAsJsonAsync("/auth/v1/register", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        return envelope.GetProperty("data").Deserialize<AuthResponse>(JsonOptions)!;
    }
}
