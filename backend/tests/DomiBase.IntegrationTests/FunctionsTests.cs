using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DomiBase.IntegrationTests;

[Collection(FunctionsCollection.Name)]
public sealed class FunctionsTests
{
    private readonly FunctionsFixture _fixture;

    public FunctionsTests(FunctionsFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateFunction_ThenList_ReturnsMetadataWithoutModuleBytes()
    {
        await _fixture.CreateFunctionAsync("listado", TestWasmModules.EchoWat);

        using var admin = _fixture.Factory.CreateAdminClient();
        var json = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/projects/{_fixture.ProjectId}/functions");

        var function = json.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "listado");
        Assert.Equal(64, function.GetProperty("moduleSha256").GetString()!.Length);
        Assert.True(function.GetProperty("enabled").GetBoolean());
        Assert.False(function.TryGetProperty("module", out _));
        Assert.False(function.TryGetProperty("moduleBase64", out _));
    }

    [Fact]
    public async Task InvokeEcho_ReturnsInputAndAnonymousContext()
    {
        await _fixture.CreateFunctionAsync("eco", TestWasmModules.EchoWat);
        using var client = _fixture.CreateRuntimeClient();

        var response = await client.PostAsJsonAsync("/functions/v1/eco", new { mensaje = "hola", numero = 42 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("hola", data.GetProperty("input").GetProperty("mensaje").GetString());
        Assert.Equal(42, data.GetProperty("input").GetProperty("numero").GetInt32());
        Assert.Equal(_fixture.ProjectId.ToString("D"), data.GetProperty("context").GetProperty("projectId").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("context").GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task AuthenticatedInvoke_IncludesUserIdInContext()
    {
        await _fixture.CreateFunctionAsync("eco-auth", TestWasmModules.EchoWat);
        var token = await _fixture.RegisterUserAsync();
        using var client = _fixture.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/functions/v1/eco-auth", new { valor = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var userId = json.RootElement
            .GetProperty("data")
            .GetProperty("context")
            .GetProperty("userId")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(userId));
        Assert.True(Guid.TryParse(userId, out _));
    }

    [Fact]
    public async Task UnknownFunction_Returns404()
    {
        using var client = _fixture.CreateRuntimeClient();
        var response = await client.PostAsJsonAsync("/functions/v1/no-existe", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("FUNCTION_NOT_FOUND", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task InvalidApiKey_Returns401()
    {
        await _fixture.CreateFunctionAsync("eco-key", TestWasmModules.EchoWat);
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("apikey", "db_public_invalid_key");

        var response = await client.PostAsJsonAsync("/functions/v1/eco-key", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_API_KEY", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task OutOfFuel_Returns422()
    {
        await _fixture.CreateFunctionAsync(
            "agota-cpu",
            TestWasmModules.SpinWat,
            new { fuel = 1000, memoryPages = 16, timeoutMs = 1000, maxOutputBytes = 65536 });

        using var client = _fixture.CreateRuntimeClient();
        var response = await client.PostAsJsonAsync("/functions/v1/agota-cpu", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FUNCTION_OUT_OF_FUEL", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ModuleWithImports_IsRejectedAtInvocation()
    {
        // The API never compiles guest code: validation happens inside the worker.
        await _fixture.CreateFunctionAsync("con-imports", TestWasmModules.ImportsWat);

        using var client = _fixture.CreateRuntimeClient();
        var response = await client.PostAsJsonAsync("/functions/v1/con-imports", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_MODULE", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ModuleExceedingMemoryLimit_IsRejectedAtInvocation()
    {
        await _fixture.CreateFunctionAsync(
            "mucha-memoria",
            TestWasmModules.LargeMemoryWat,
            new { fuel = 1000000, memoryPages = 1, timeoutMs = 1000, maxOutputBytes = 65536 });

        using var client = _fixture.CreateRuntimeClient();
        var response = await client.PostAsJsonAsync("/functions/v1/mucha-memoria", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_MODULE", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task SecondProject_CannotInvokeFirstProjectFunction()
    {
        await _fixture.CreateFunctionAsync("privada", TestWasmModules.EchoWat);
        var (otherApiKey, otherProjectId) = await _fixture.CreateSecondProjectAsync();

        try
        {
            using var client = _fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("apikey", otherApiKey);
            var response = await client.PostAsJsonAsync("/functions/v1/privada", new { });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("FUNCTION_NOT_FOUND", await ErrorCodeAsync(response));
        }
        finally
        {
            await _fixture.CleanupSecondProjectAsync(otherProjectId);
        }
    }

    [Fact]
    public async Task DeleteFunction_RemovesIt()
    {
        var id = await _fixture.CreateFunctionAsync("temporal", TestWasmModules.EchoWat);

        using var admin = _fixture.Factory.CreateAdminClient();
        var delete = await admin.DeleteAsync($"/api/v1/projects/{_fixture.ProjectId}/functions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var client = _fixture.CreateRuntimeClient();
        var invoke = await client.PostAsJsonAsync("/functions/v1/temporal", new { });
        Assert.Equal(HttpStatusCode.NotFound, invoke.StatusCode);
    }

    [Fact]
    public async Task InvalidJsonInput_Returns400()
    {
        await _fixture.CreateFunctionAsync("eco-json", TestWasmModules.EchoWat);
        using var client = _fixture.CreateRuntimeClient();

        var response = await client.PostAsync(
            "/functions/v1/eco-json",
            new StringContent("no-es-json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_INPUT", await ErrorCodeAsync(response));
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
