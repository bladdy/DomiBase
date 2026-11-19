using System.Data;
using System.Text.RegularExpressions;
using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;
using Npgsql;

namespace DomiBase.Provisioning.Catalog;

/// <summary>
/// Reads live extension state from one project database with the privileged provisioning
/// connection. The extension names come exclusively from the application allowlist, are
/// parameterized as an array and never become SQL identifiers. A read-only transaction is
/// the final guard against accidental mutation.
/// </summary>
public sealed class ProjectExtensionCatalogReader(IProvisioningConnectionFactory connectionFactory)
    : IProjectExtensionCatalog
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex DatabaseNamePattern =
        new(@"^domibase_[a-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string CatalogSql = """
        SELECT requested.name,
               available.default_version,
               extension_version.trusted,
               installed.extversion,
               installed.oid IS NOT NULL,
               available.name IS NOT NULL
        FROM unnest(@names::text[]) AS requested(name)
        LEFT JOIN pg_catalog.pg_available_extensions AS available
               ON available.name = requested.name
        LEFT JOIN pg_catalog.pg_available_extension_versions AS extension_version
               ON extension_version.name = requested.name
              AND extension_version.version = available.default_version
        LEFT JOIN pg_catalog.pg_extension AS installed
               ON installed.extname = requested.name
        ORDER BY requested.name
        """;

    public async Task<IReadOnlyList<PostgresExtension>> ListAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        if (!DatabaseNamePattern.IsMatch(database))
        {
            throw new ExtensionExecutionException(
                "The project database name is not valid.",
                ControlErrorCode.Validation);
        }

        await using var connection = await connectionFactory.OpenAsync(database, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(CatalogSql, connection, transaction)
            {
                CommandTimeout = (int)CommandTimeout.TotalSeconds
            };
            command.Parameters.AddWithValue("names", PostgresExtensionCatalog.Names.ToArray());

            var extensions = new List<PostgresExtension>();
            await using (var result = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await result.ReadAsync(cancellationToken))
                {
                    var name = result.GetString(0);
                    extensions.Add(new PostgresExtension(
                        name,
                        PostgresExtensionCatalog.Describe(name),
                        !result.IsDBNull(2) && result.GetBoolean(2),
                        result.IsDBNull(1) ? null : result.GetString(1),
                        result.IsDBNull(3) ? null : result.GetString(3),
                        !result.IsDBNull(5) && result.GetBoolean(5),
                        !result.IsDBNull(4) && result.GetBoolean(4)));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return extensions;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new ExtensionExecutionException(
                "The extension catalog could not be read for this project.",
                ControlErrorCode.Conflict);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
