using DomiBase.Application.Functions;
using DomiBase.Domain.Functions;

namespace DomiBase.UnitTests.Functions;

public sealed class FunctionServiceTests
{
    private static readonly byte[] Module = [0, 97, 115, 109, 1, 0, 0, 0];
    private static FunctionOptions Options => new()
    {
        WorkerPath = "worker.dll",
        WorkerStartupTimeoutMs = 5000,
        MaxModuleSizeBytes = 1024,
        DefaultFuel = 1_000_000,
        DefaultMemoryPages = 16,
        DefaultTimeoutMs = 1000,
        MaxInputBytes = 4096,
        MaxOutputBytes = 4096,
        MaxConcurrency = 2
    };

    [Fact]
    public async Task CreateAsync_ValidatesNameAndModule()
    {
        var service = new FunctionService(new FakeRepository(), new FakeExecutor(), Options);

        var invalidName = await service.CreateAsync(Guid.NewGuid(), "Mala Nombre!", null, Base64(), null);
        Assert.False(invalidName.Succeeded);
        Assert.Equal("INVALID_FUNCTION_NAME", invalidName.Code);

        var invalidBase64 = await service.CreateAsync(Guid.NewGuid(), "valida", null, "!!!", null);
        Assert.False(invalidBase64.Succeeded);
        Assert.Equal("INVALID_MODULE", invalidBase64.Code);

        var tooLarge = await service.CreateAsync(Guid.NewGuid(), "valida", null, Base64(new byte[1025]), null);
        Assert.False(tooLarge.Succeeded);
        Assert.Equal("MODULE_TOO_LARGE", tooLarge.Code);
    }

    [Fact]
    public async Task CreateAsync_NormalizesNameAndComputesHash()
    {
        var repository = new FakeRepository();
        var service = new FunctionService(repository, new FakeExecutor(), Options);

        var result = await service.CreateAsync(
            Guid.NewGuid(),
            "  Saludo-1  ",
            "descripcion",
            Base64(),
            new FunctionLimitInput { Fuel = 500, MemoryPages = 4, TimeoutMs = 200 });

        Assert.True(result.Succeeded);
        Assert.Equal("saludo-1", result.Value!.Name);
        Assert.Equal(64, result.Value.ModuleSha256.Length);
        Assert.Equal(500, result.Value.Fuel);
        Assert.Equal(4, result.Value.MemoryPages);
        Assert.Equal(200, result.Value.TimeoutMs);
        Assert.Equal(4096, result.Value.MaxInputBytes);
        Assert.True(result.Value.Enabled);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateNameAndInvalidLimits()
    {
        var projectId = Guid.NewGuid();
        var repository = new FakeRepository();
        var service = new FunctionService(repository, new FakeExecutor(), Options);

        Assert.True((await service.CreateAsync(projectId, "saludo", null, Base64(), null)).Succeeded);

        var duplicate = await service.CreateAsync(projectId, "saludo", null, Base64(), null);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("FUNCTION_ALREADY_EXISTS", duplicate.Code);

        var badLimits = await service.CreateAsync(
            projectId,
            "otro",
            null,
            Base64(),
            new FunctionLimitInput { MemoryPages = 0 });
        Assert.False(badLimits.Succeeded);
        Assert.Equal("INVALID_LIMITS", badLimits.Code);
    }

    [Fact]
    public async Task InvokeAsync_ResolvesDefinitionAndForwardsContext()
    {
        var projectId = Guid.NewGuid();
        var executor = new FakeExecutor();
        var repository = new FakeRepository();
        var service = new FunctionService(repository, executor, Options);

        await service.CreateAsync(projectId, "saludo", null, Base64(), null);
        var result = await service.InvokeAsync(projectId, "saludo", "{\"x\":1}", "user-42");

        Assert.True(result.Succeeded);
        Assert.Equal("{\"ok\":true}", result.Value);
        Assert.NotNull(executor.Request);
        Assert.Equal(projectId, executor.Request!.ProjectId);
        Assert.Equal("user-42", executor.Request.UserId);
        Assert.Equal("{\"x\":1}", executor.Request.InputJson);
        Assert.Equal(Module, executor.Request.Module);
        Assert.Equal(1_000_000, executor.Request.Limits.Fuel);
    }

    [Fact]
    public async Task InvokeAsync_RejectsMissingDisabledAndOversizedFunctions()
    {
        var projectId = Guid.NewGuid();
        var repository = new FakeRepository();
        var service = new FunctionService(repository, new FakeExecutor(), Options);

        var missing = await service.InvokeAsync(projectId, "no-existe", "null", null);
        Assert.Equal("FUNCTION_NOT_FOUND", missing.Code);

        var created = await service.CreateAsync(projectId, "saludo", null, Base64(), null);
        created.Value!.Enabled = false;
        var disabled = await service.InvokeAsync(projectId, "saludo", "null", null);
        Assert.Equal("FUNCTION_DISABLED", disabled.Code);

        created.Value.Enabled = true;
        var oversized = await service.InvokeAsync(projectId, "saludo", new string('x', 5000), null);
        Assert.Equal("INPUT_TOO_LARGE", oversized.Code);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesExecutorFailures()
    {
        var projectId = Guid.NewGuid();
        var executor = new FakeExecutor
        {
            FailureCode = "FUNCTION_OUT_OF_FUEL",
            FailureMessage = "CPU budget exceeded."
        };
        var service = new FunctionService(new FakeRepository(), executor, Options);

        await service.CreateAsync(projectId, "saludo", null, Base64(), null);
        var result = await service.InvokeAsync(projectId, "saludo", "null", null);

        Assert.False(result.Succeeded);
        Assert.Equal("FUNCTION_OUT_OF_FUEL", result.Code);
        Assert.Equal("CPU budget exceeded.", result.Message);
    }

    private static string Base64(byte[]? module = null) =>
        Convert.ToBase64String(module ?? Module);

    private sealed class FakeRepository : IFunctionRepository
    {
        private readonly List<FunctionDefinition> _definitions = [];

        public Task<IReadOnlyList<FunctionDefinition>> ListAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FunctionDefinition>>(
                _definitions.Where(f => f.ProjectId == projectId).ToList());

        public Task<FunctionDefinition?> FindByNameAsync(
            Guid projectId,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_definitions.FirstOrDefault(f => f.ProjectId == projectId && f.Name == name));

        public Task<FunctionDefinition?> FindByIdAsync(
            Guid projectId,
            Guid functionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_definitions.FirstOrDefault(f => f.ProjectId == projectId && f.Id == functionId));

        public Task<FunctionDefinition> CreateAsync(
            FunctionDefinition definition,
            CancellationToken cancellationToken = default)
        {
            _definitions.Add(definition);
            return Task.FromResult(definition);
        }

        public Task<bool> DeleteAsync(
            Guid projectId,
            Guid functionId,
            CancellationToken cancellationToken = default)
        {
            var found = _definitions.FirstOrDefault(f => f.ProjectId == projectId && f.Id == functionId);
            if (found is null)
            {
                return Task.FromResult(false);
            }

            _definitions.Remove(found);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeExecutor : IFunctionExecutor
    {
        public FunctionExecutionRequest? Request { get; private set; }

        public string? FailureCode { get; init; }

        public string FailureMessage { get; init; } = "failure";

        public Task<FunctionExecutionResult> ExecuteAsync(
            FunctionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(FailureCode is null
                ? FunctionExecutionResult.Ok("{\"ok\":true}", 10, 1)
                : FunctionExecutionResult.Fail(FailureCode, FailureMessage));
        }
    }
}
