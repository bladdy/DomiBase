using DomiBase.Domain.Functions;

namespace DomiBase.Application.Functions;

public interface IFunctionRepository
{
    Task<IReadOnlyList<FunctionDefinition>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<FunctionDefinition?> FindByNameAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default);

    Task<FunctionDefinition?> FindByIdAsync(
        Guid projectId,
        Guid functionId,
        CancellationToken cancellationToken = default);

    Task<FunctionDefinition> CreateAsync(
        FunctionDefinition definition,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid projectId,
        Guid functionId,
        CancellationToken cancellationToken = default);
}
