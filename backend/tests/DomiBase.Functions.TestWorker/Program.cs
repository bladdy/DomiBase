// Test-only worker used to exercise process-level timeout and crash handling.
// It follows the same stdin/stdout contract as DomiBase.Functions.Worker.
var request = await Console.In.ReadToEndAsync();

if (request.Contains("crash", StringComparison.Ordinal))
{
    Environment.Exit(3);
}

await Task.Delay(10_000);
Console.Out.Write("{\"outputBase64\":null,\"fuelConsumed\":0,\"durationMs\":10000,\"error\":null}");
