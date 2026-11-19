using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace DomiBase.IntegrationTests;

[Collection(RealtimeCollection.Name)]
public sealed class RealtimeTests(RealtimeFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Subscribe_AndInsert_ReceivesChangeEvent()
    {
        var connection = CreateConnection(fixture.ApiKey);

        var received = new List<JsonElement>();
        connection.On<JsonElement>("change", payload => received.Add(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", "eventos");

        var client = fixture.CreateRuntimeClient();
        var insertResponse = await client.PostAsJsonAsync("/rest/v1/eventos", new { nombre = "test" });
        Assert.Equal(HttpStatusCode.Created, insertResponse.StatusCode);

        // Wait for the notification to propagate.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Single(received);
        var payload = received[0];
        Assert.Equal("eventos", payload.GetProperty("table").GetString());
        Assert.Equal("INSERT", payload.GetProperty("operation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("rowId").GetString()));
        Assert.True(payload.GetProperty("timestamp").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MissingApiKey_RejectsSubscription()
    {
        var connection = CreateConnection(apiKey: null);

        await connection.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", "eventos"));
        Assert.Contains("closed the connection", exception.Message, StringComparison.OrdinalIgnoreCase);

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Subscribe_ToNonExposedTable_Fails()
    {
        var connection = CreateConnection(fixture.ApiKey);

        await connection.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", "no_existe"));
        Assert.Contains("Table is not exposed", exception.Message);

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    private HubConnection CreateConnection(string? apiKey)
    {
        var baseUrl = fixture.Factory.Server.BaseAddress.ToString().TrimEnd('/') + "/realtime/v1";

        return new HubConnectionBuilder()
            .WithUrl(baseUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    options.Headers.Add("apikey", apiKey);
                }

                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents;
                options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
            })
            .Build();
    }
}
