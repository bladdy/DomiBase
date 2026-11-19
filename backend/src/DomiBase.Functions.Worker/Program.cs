using System.Text.Json;
using DomiBase.Application.Functions;
using DomiBase.Functions.Worker;

// One request per process. The API writes the request to stdin and reads the
// single JSON response from stdout. No WASI, no sockets, no filesystem access.
var input = await Console.In.ReadToEndAsync();

FunctionWorkerResponse response;
try
{
    if (input.Length > 16 * 1024 * 1024)
    {
        throw new FormatException("The worker request is too large.");
    }

    var request = FunctionWorkerProtocol.ParseRequest(input);
    response = WasmFunctionRunner.Run(request);
}
catch (JsonException)
{
    response = Failure("INVALID_REQUEST", "The worker request is not valid JSON.");
}
catch (FormatException ex)
{
    response = Failure("INVALID_REQUEST", ex.Message);
}
catch (Exception)
{
    // Never leak stack traces or host details to the caller.
    response = Failure("WORKER_ERROR", "The function could not be executed.");
}

Console.Out.Write(FunctionWorkerProtocol.SerializeResponse(response));

static FunctionWorkerResponse Failure(string code, string message) =>
    new()
    {
        Error = new FunctionWorkerError { Code = code, Message = message }
    };
