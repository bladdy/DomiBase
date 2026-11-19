using DomiBase.Application.Functions;
using DomiBase.Domain.Functions;
using Microsoft.EntityFrameworkCore;

namespace DomiBase.Infrastructure.Persistence;

public sealed class EfFunctionRepository(DomiBaseDbContext db) : IFunctionRepository
{
    public async Task<IReadOnlyList<FunctionDefinition>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.Functions
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

    public async Task<FunctionDefinition?> FindByNameAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default) =>
        await db.Functions
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Name == name, cancellationToken);

    public async Task<FunctionDefinition?> FindByIdAsync(
        Guid projectId,
        Guid functionId,
        CancellationToken cancellationToken = default) =>
        await db.Functions
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == functionId, cancellationToken);

    public async Task<FunctionDefinition> CreateAsync(
        FunctionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        db.Functions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);
        return definition;
    }

    public async Task<bool> DeleteAsync(
        Guid projectId,
        Guid functionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await db.Functions
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == functionId, cancellationToken);

        if (definition is null)
        {
            return false;
        }

        db.Functions.Remove(definition);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
