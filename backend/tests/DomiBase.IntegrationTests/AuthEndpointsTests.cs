using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Api.Endpoints;

namespace DomiBase.IntegrationTests;

[Collection(AuthRepositoryCollection.Name)]
public sealed class AuthEndpointsTests(AuthRepositoryFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Register_ThenLogin_ReturnsTokens()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var registerResponse = await PostAsync("/auth/v1/register", new { email, password });
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        var registerBody = await DeserializeAsync<AuthResponse>(registerResponse);
        Assert.False(string.IsNullOrWhiteSpace(registerBody.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(registerBody.RefreshToken));
        Assert.NotNull(registerBody.User);
        Assert.Equal(email.ToLowerInvariant(), registerBody.User.Email);

        var loginResponse = await PostAsync("/auth/v1/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var loginBody = await DeserializeAsync<AuthResponse>(loginResponse);
        Assert.False(string.IsNullOrWhiteSpace(loginBody.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(loginBody.RefreshToken));
        Assert.Equal(registerBody.User.Id, loginBody.User!.Id);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var first = await PostAsync("/auth/v1/register", new { email, password });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAsync("/auth/v1/register", new { email, password });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Login_InvalidCredentials_ReturnsUnauthorized()
    {
        var email = $"{Guid.NewGuid():N}@example.com";

        var response = await PostAsync("/auth/v1/login", new { email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesTokens_And_Reuse_RevokesFamily()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var registerResponse = await PostAsync("/auth/v1/register", new { email, password });
        var session = await DeserializeAsync<AuthResponse>(registerResponse);

        var refreshResponse = await PostAsync("/auth/v1/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var refreshed = await DeserializeAsync<AuthResponse>(refreshResponse);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));
        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken);

        // Reusing the original refresh token must now fail and revoke the family.
        var reuseResponse = await PostAsync("/auth/v1/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);

        // Even the legitimately rotated token should be revoked because the whole family is revoked.
        var rotatedReuseResponse = await PostAsync("/auth/v1/refresh", new { refreshToken = refreshed.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedReuseResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var registerResponse = await PostAsync("/auth/v1/register", new { email, password });
        var session = await DeserializeAsync<AuthResponse>(registerResponse);

        var logoutResponse = await PostAsync("/auth/v1/logout", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        var refreshAfterLogout = await PostAsync("/auth/v1/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task MissingApiKey_ReturnsUnauthorized()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/v1/register", new { email = "a@b.com", password = "12345678" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", fixture.ApiKey);
        return await client.PostAsJsonAsync(path, body);
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        return envelope.GetProperty("data").Deserialize<T>(JsonOptions)!;
    }
}
