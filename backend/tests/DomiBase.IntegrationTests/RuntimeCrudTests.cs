using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DomiBase.IntegrationTests;

[Collection(RuntimeCollection.Name)]
public class RuntimeCrudTests(RuntimeProjectFixture fixture)
{
    [Fact]
    public async Task CrudLifecycle_WorksEndToEnd()
    {
        using var client = fixture.CreateRuntimeClientWithKey();
        var email = UniqueEmail();

        var insert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos",
            new { nombre = "Ana", email, edad = 34 });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        Guid id;
        using (var json = await ReadJsonAsync(insert))
        {
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            var row = root.GetProperty("data")[0];
            id = row.GetProperty("id").GetGuid();
            Assert.Equal("Ana", row.GetProperty("nombre").GetString());
            Assert.Equal("nuevo", row.GetProperty("estado").GetString());
            Assert.Equal(34, row.GetProperty("edad").GetInt32());
        }

        var select = await client.GetAsync(
            $"/rest/v1/contactos?select=id,email,estado&f=email.eq.{Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);

        using (var json = await ReadJsonAsync(select))
        {
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            var row = root.GetProperty("data")[0];
            Assert.Equal(id, row.GetProperty("id").GetGuid());
            Assert.Equal(email, row.GetProperty("email").GetString());
            Assert.Equal("nuevo", row.GetProperty("estado").GetString());
            Assert.False(row.TryGetProperty("nombre", out _));
        }

        var update = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"/rest/v1/contactos?select=id,estado&f=id.eq.{id}",
            new { estado = "pagado" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using (var json = await ReadJsonAsync(update))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(
                "pagado",
                json.RootElement.GetProperty("data")[0].GetProperty("estado").GetString());
        }

        var reload = await client.GetAsync($"/rest/v1/contactos?select=estado&f=id.eq.{id}");
        using (var json = await ReadJsonAsync(reload))
        {
            Assert.Equal("pagado", json.RootElement.GetProperty("data")[0].GetProperty("estado").GetString());
        }

        var delete = await client.DeleteAsync($"/rest/v1/contactos?select=id&f=id.eq.{id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using (var json = await ReadJsonAsync(delete))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(id, json.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());
        }

        var empty = await client.GetAsync($"/rest/v1/contactos?f=id.eq.{id}");
        using (var json = await ReadJsonAsync(empty))
        {
            Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("data").EnumerateArray());
        }
    }

    [Fact]
    public async Task BulkInsert_AndInFilter_Work()
    {
        using var client = fixture.CreateRuntimeClientWithKey();
        var first = UniqueEmail();
        var second = UniqueEmail();

        var insert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos?select=id,email",
            new object[]
            {
                new { nombre = "Bulk One", email = first },
                new { nombre = "Bulk Two", email = second }
            });

        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        using (var json = await ReadJsonAsync(insert))
        {
            Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("data").GetArrayLength());
        }

        var select = await client.GetAsync(
            $"/rest/v1/contactos?select=email&f=email.in.{Uri.EscapeDataString(first)},{Uri.EscapeDataString(second)}&order=email.asc");
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);

        using (var json = await ReadJsonAsync(select))
        {
            Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
        }

        var delete = await client.DeleteAsync(
            $"/rest/v1/contactos?f=email.in.{Uri.EscapeDataString(first)},{Uri.EscapeDataString(second)}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var deleteJson = await ReadJsonAsync(delete);
        Assert.Equal(2, deleteJson.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task FiltersAndOrdering_SupportAllowlistedOperators()
    {
        using var client = fixture.CreateRuntimeClientWithKey();
        var prefix = "Pfx" + Guid.NewGuid().ToString("N")[..8];

        var insert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos?select=id,nombre",
            new object[]
            {
                new { nombre = $"{prefix}-alpha", email = UniqueEmail(), estado = "nuevo", edad = 10, activo = true },
                new { nombre = $"{prefix}-beta", email = UniqueEmail(), estado = "pagado", edad = (int?)null, activo = true },
                new { nombre = $"{prefix}-gamma", email = UniqueEmail(), estado = "nuevo", edad = 40, activo = true }
            });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);

        var prefixPattern = Uri.EscapeDataString(prefix + "%");
        var lowerPrefixPattern = Uri.EscapeDataString(prefix.ToLowerInvariant() + "%");

        async Task<int> CountAsync(string query)
        {
            var response = await client.GetAsync($"/rest/v1/contactos?select=nombre&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await ReadJsonAsync(response);
            return json.RootElement.GetProperty("count").GetInt32();
        }

        Assert.Equal(3, await CountAsync($"f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(3, await CountAsync($"f=nombre.like.{prefixPattern}"));
        Assert.Equal(1, await CountAsync($"f=estado.neq.nuevo&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(1, await CountAsync($"f=edad.gt.10&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(2, await CountAsync($"f=edad.gte.10&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(1, await CountAsync($"f=edad.lt.40&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(2, await CountAsync($"f=edad.lte.40&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(1, await CountAsync($"f=edad.is.null&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(3, await CountAsync($"f=activo.is.true&f=nombre.ilike.{lowerPrefixPattern}"));
        Assert.Equal(3, await CountAsync($"f=estado.in.nuevo,pagado&f=nombre.ilike.{lowerPrefixPattern}"));

        var ordered = await client.GetAsync(
            $"/rest/v1/contactos?select=nombre&f=nombre.ilike.{lowerPrefixPattern}&order=nombre.asc&limit=2&offset=1");
        Assert.Equal(HttpStatusCode.OK, ordered.StatusCode);

        using (var json = await ReadJsonAsync(ordered))
        {
            Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
            var names = json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(row => row.GetProperty("nombre").GetString())
                .ToList();
            Assert.Equal(new[] { $"{prefix}-beta", $"{prefix}-gamma" }, names);
        }

        var cleanup = await client.DeleteAsync(
            $"/rest/v1/contactos?f=nombre.ilike.{lowerPrefixPattern}");
        using var cleanupJson = await ReadJsonAsync(cleanup);
        Assert.Equal(3, cleanupJson.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task WritesRequireFilters_AndInvalidRequestsAreRejected()
    {
        using var client = fixture.CreateRuntimeClientWithKey();

        var patchWithoutFilter = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            "/rest/v1/contactos",
            new { estado = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, patchWithoutFilter.StatusCode);
        Assert.Equal("FILTER_REQUIRED", await ErrorCodeAsync(patchWithoutFilter));

        var deleteWithoutFilter = await client.DeleteAsync("/rest/v1/contactos");
        Assert.Equal(HttpStatusCode.BadRequest, deleteWithoutFilter.StatusCode);
        Assert.Equal("FILTER_REQUIRED", await ErrorCodeAsync(deleteWithoutFilter));

        Assert.Equal(
            "INVALID_LIMIT",
            await GetErrorAsync(client, "/rest/v1/contactos?limit=1001"));

        Assert.Equal(
            "INVALID_ORDER",
            await GetErrorAsync(client, "/rest/v1/contactos?order=nombre.sideways"));

        Assert.Equal(
            "INVALID_COLUMN",
            await GetErrorAsync(client, "/rest/v1/contactos?select=password"));

        Assert.Equal(
            "INVALID_FILTER_OPERATOR",
            await GetErrorAsync(client, "/rest/v1/contactos?f=estado.raw.nuevo"));

        Assert.Equal(
            "INVALID_VALUE",
            await GetErrorAsync(client, "/rest/v1/contactos?f=id.eq.not-a-uuid"));

        var readOnlyPatch = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            "/rest/v1/solo_lectura?f=valor.eq.x",
            new { valor = "y" });
        Assert.Equal(HttpStatusCode.NotFound, readOnlyPatch.StatusCode);

        var readOnlyInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/solo_lectura",
            new { valor = "y" });
        Assert.Equal(HttpStatusCode.NotFound, readOnlyInsert.StatusCode);

        var malformed = await SendRawAsync(client, HttpMethod.Post, "/rest/v1/contactos", "{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("INVALID_BODY", await ErrorCodeAsync(malformed));

        var patchWithArray = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            "/rest/v1/contactos?f=id.eq.11111111-1111-1111-1111-111111111111",
            new object[] { new { estado = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, patchWithArray.StatusCode);
        Assert.Equal("INVALID_BODY", await ErrorCodeAsync(patchWithArray));

        var missingName = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos",
            new { email = UniqueEmail() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingName.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(missingName));

        var email = UniqueEmail();
        var firstInsert = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos?select=id",
            new { nombre = "Duplicado", email });
        Assert.Equal(HttpStatusCode.Created, firstInsert.StatusCode);

        var duplicate = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/rest/v1/contactos",
            new { nombre = "Duplicado", email });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("CONFLICT", await ErrorCodeAsync(duplicate));

        using (var firstJson = await ReadJsonAsync(firstInsert))
        {
            var id = firstJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
            await client.DeleteAsync($"/rest/v1/contactos?f=id.eq.{id}");
        }

        var tooManyRows = Enumerable.Range(0, 101)
            .Select(index => new { nombre = $"bulk-{index}" })
            .ToArray();
        var bulk = await SendJsonAsync(client, HttpMethod.Post, "/rest/v1/contactos", tooManyRows);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bulk.StatusCode);
        Assert.Equal("PAYLOAD_TOO_LARGE", await ErrorCodeAsync(bulk));

        var emptyArray = await SendRawAsync(client, HttpMethod.Post, "/rest/v1/contactos", "[]");
        Assert.Equal(HttpStatusCode.BadRequest, emptyArray.StatusCode);
        Assert.Equal("INVALID_BODY", await ErrorCodeAsync(emptyArray));
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object body)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body)
        };

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string rawJson)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json")
        };

        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<string?> GetErrorAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await ErrorCodeAsync(response);
    }

    private static string UniqueEmail() => $"contact-{Guid.NewGuid():N}@example.test";
}
