using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DomiBase.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c> commands. Reads
/// <c>CONTROL_DB_CONNECTION_STRING</c> from the environment or the nearest local <c>.env</c>.
/// Never hardcode real credentials here.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DomiBaseDbContext>
{
    public DomiBaseDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTROL_DB_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var dotEnvPath = Configuration.DotEnv.Find(Directory.GetCurrentDirectory());
            if (dotEnvPath is not null)
            {
                Configuration.DotEnv.ParseFile(dotEnvPath)
                    .TryGetValue("CONTROL_DB_CONNECTION_STRING", out connectionString);
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "CONTROL_DB_CONNECTION_STRING is not configured. Set the environment variable or add it to the local .env file.");
        }

        var options = new DbContextOptionsBuilder<DomiBaseDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new DomiBaseDbContext(options);
    }
}
