using System.Data;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure.Runtime;
using Npgsql;

namespace DomiBase.Infrastructure.Auth;

/// <summary>
/// SQL access to the per-project <c>auth</c> schema through the project's restricted
/// runtime login. All identifiers are allow-listed and all values are parametrized.
/// </summary>
public sealed class NpgsqlAuthUserRepository(RuntimeConnectionFactory connections) : IAuthUserRepository
{
    private const string UsersColumns = "id, email, password_hash, created_at, disabled_at";
    private const string RefreshTokenColumns = "id, user_id, family_id, token_hash, created_at, expires_at, consumed_at, revoked_at";

    public async Task<AuthUserRecord?> FindByEmailAsync(
        RuntimeProjectContext context,
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {UsersColumns} FROM auth.users WHERE lower(email) = lower(@email)",
            connection);
        command.Parameters.AddWithValue("email", normalizedEmail);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadUserAsync(reader, cancellationToken);
    }

    public async Task<AuthUserRecord?> FindByIdAsync(
        RuntimeProjectContext context,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {UsersColumns} FROM auth.users WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadUserAsync(reader, cancellationToken);
    }

    public async Task<AuthUserRecord?> CreateUserAsync(
        RuntimeProjectContext context,
        string normalizedEmail,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        var userId = Guid.CreateVersion7();

        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.users (id, email, password_hash)
            VALUES (@id, @email, @passwordHash)
            ON CONFLICT (lower(email)) DO NOTHING
            RETURNING id, email, password_hash, created_at, disabled_at
            """,
            connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", normalizedEmail);
        command.Parameters.AddWithValue("passwordHash", passwordHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadUserAsync(reader, cancellationToken);
    }

    public async Task CreateRefreshTokenAsync(
        RuntimeProjectContext context,
        Guid userId,
        Guid familyId,
        string tokenHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.refresh_tokens (id, user_id, family_id, token_hash, expires_at)
            VALUES (@id, @userId, @familyId, @tokenHash, @expiresAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("familyId", familyId);
        command.Parameters.AddWithValue("tokenHash", tokenHash);
        command.Parameters.AddWithValue("expiresAt", expiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RefreshTokenRecord?> FindRefreshTokenAsync(
        RuntimeProjectContext context,
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {RefreshTokenColumns} FROM auth.refresh_tokens WHERE token_hash = @tokenHash",
            connection);
        command.Parameters.AddWithValue("tokenHash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadRefreshTokenAsync(reader, cancellationToken);
    }

    public async Task<bool> RotateRefreshTokenAsync(
        RuntimeProjectContext context,
        Guid tokenId,
        Guid userId,
        Guid familyId,
        string newTokenHash,
        DateTimeOffset consumedAt,
        DateTimeOffset newExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            // Lock the row and verify it is still fresh. If another request consumed it
            // concurrently, or it was revoked, the rotation fails and the caller revokes
            // the whole family (reuse detection).
            await using (var checkCommand = new NpgsqlCommand(
                             """
                             SELECT id FROM auth.refresh_tokens
                             WHERE id = @id AND user_id = @userId AND family_id = @familyId
                               AND consumed_at IS NULL AND revoked_at IS NULL
                             FOR UPDATE
                             """,
                             connection,
                             transaction))
            {
                checkCommand.Parameters.AddWithValue("id", tokenId);
                checkCommand.Parameters.AddWithValue("userId", userId);
                checkCommand.Parameters.AddWithValue("familyId", familyId);

                if (await checkCommand.ExecuteScalarAsync(cancellationToken) is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await using (var consumeCommand = new NpgsqlCommand(
                             "UPDATE auth.refresh_tokens SET consumed_at = @consumedAt WHERE id = @id",
                             connection,
                             transaction))
            {
                consumeCommand.Parameters.AddWithValue("consumedAt", consumedAt);
                consumeCommand.Parameters.AddWithValue("id", tokenId);
                await consumeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertCommand = new NpgsqlCommand(
                             """
                             INSERT INTO auth.refresh_tokens (id, user_id, family_id, token_hash, expires_at)
                             VALUES (@id, @userId, @familyId, @tokenHash, @expiresAt)
                             """,
                             connection,
                             transaction))
            {
                insertCommand.Parameters.AddWithValue("id", Guid.CreateVersion7());
                insertCommand.Parameters.AddWithValue("userId", userId);
                insertCommand.Parameters.AddWithValue("familyId", familyId);
                insertCommand.Parameters.AddWithValue("tokenHash", newTokenHash);
                insertCommand.Parameters.AddWithValue("expiresAt", newExpiresAt);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            await RollbackQuietlyAsync(transaction);
            throw;
        }
        catch (Exception)
        {
            await RollbackQuietlyAsync(transaction);
            throw;
        }
    }

    public async Task RevokeFamilyAsync(
        RuntimeProjectContext context,
        Guid familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE auth.refresh_tokens SET revoked_at = @revokedAt WHERE family_id = @familyId",
            connection);
        command.Parameters.AddWithValue("revokedAt", revokedAt);
        command.Parameters.AddWithValue("familyId", familyId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeRefreshTokenAsync(
        RuntimeProjectContext context,
        string tokenHash,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(context, cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE auth.refresh_tokens SET revoked_at = @revokedAt WHERE token_hash = @tokenHash",
            connection);
        command.Parameters.AddWithValue("revokedAt", revokedAt);
        command.Parameters.AddWithValue("tokenHash", tokenHash);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AuthUserRecord?> ReadUserAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthUserRecord(
            Id: reader.GetGuid(0),
            Email: reader.GetString(1),
            PasswordHash: reader.GetString(2),
            CreatedAt: reader.GetDateTime(3),
            DisabledAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    private static async Task<RefreshTokenRecord?> ReadRefreshTokenAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RefreshTokenRecord(
            Id: reader.GetGuid(0),
            UserId: reader.GetGuid(1),
            FamilyId: reader.GetGuid(2),
            ExpiresAt: reader.GetDateTime(5),
            ConsumedAt: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            RevokedAt: reader.IsDBNull(7) ? null : reader.GetDateTime(7));
    }

    private static async Task RollbackQuietlyAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // Connection already broken; disposing it is enough.
        }
    }
}
