using System.Data;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Infrastructure.Realtime;

/// <summary>
/// SignalR hub at <c>/realtime/v1</c>. Each connection is scoped to one project
/// resolved from the public <c>apikey</c>; an optional Bearer token is validated
/// to preserve authenticated context for subscription authorization.
/// </summary>
public sealed class RealtimeHub : Hub
{
    internal const string ApiKeyQueryName = "apikey";
    internal const string ProjectContextItem = "domibase:project-context";
    internal const string UserIdItem = "domibase:user-id";

    private const int MaxTableNameLength = 63;

    private readonly IRuntimeProjectResolver _resolver;
    private readonly IAuthSigningKeyService _signingKeys;
    private readonly IAccessTokenService _accessTokens;
    private readonly RuntimeConnectionFactory _connections;
    private readonly ILogger<RealtimeHub> _logger;

    public RealtimeHub(
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        RuntimeConnectionFactory connections,
        ILogger<RealtimeHub> logger)
    {
        _resolver = resolver;
        _signingKeys = signingKeys;
        _accessTokens = accessTokens;
        _connections = connections;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            throw new HubException("Missing HTTP context.");
        }

        // The rate limiter already resolved the key for this handshake. Reuse that decision;
        // the fallback keeps direct hub invocations working outside the HTTP pipeline.
        var requestResolution = httpContext.Items[RuntimeRequestResolution.HttpItemKey]
            as RuntimeRequestResolution;

        RuntimeResolveResult resolved;
        if (requestResolution is not null)
        {
            if (string.IsNullOrWhiteSpace(requestResolution.ApiKey))
            {
                throw new HubException("Missing apikey.");
            }

            resolved = requestResolution.Result;
        }
        else
        {
            var apiKey = ReadApiKey(httpContext);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new HubException("Missing apikey.");
            }

            resolved = await _resolver.ResolveAsync(apiKey, Context.ConnectionAborted);
        }

        if (resolved.Status != RuntimeResolveStatus.Success || resolved.Context is null)
        {
            throw new HubException("Invalid or revoked apikey, or project is not ready.");
        }

        await _resolver.TouchAsync(resolved.Context.ApiKeyId, Context.ConnectionAborted);

        Context.Items[ProjectContextItem] = resolved.Context;

        var bearer = ReadBearer(httpContext);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var claims = await ValidateBearerAsync(resolved.Context, bearer, Context.ConnectionAborted);
            if (claims is not null)
            {
                Context.Items[UserIdItem] = claims.UserId;
            }
        }

        await base.OnConnectedAsync();
    }

    public async Task Subscribe(string table)
    {
        var context = RequireContext();
        await AuthorizeTableAsync(context, table, "SELECT");

        var group = SignalRRealtimeNotifier.GroupName(context.ProjectContext.ProjectId, table);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public async Task Unsubscribe(string table)
    {
        var context = RequireContext();
        var group = SignalRRealtimeNotifier.GroupName(context.ProjectContext.ProjectId, table);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    private RealtimeConnectionContext RequireContext()
    {
        if (!Context.Items.TryGetValue(ProjectContextItem, out var contextValue) ||
            contextValue is not RuntimeProjectContext projectContext)
        {
            throw new HubException("Connection is not authorized.");
        }

        Context.Items.TryGetValue(UserIdItem, out var userValue);
        return new RealtimeConnectionContext(projectContext, userValue as Guid?);
    }

    private async Task AuthorizeTableAsync(
        RealtimeConnectionContext context,
        string table,
        string privilege)
    {
        if (string.IsNullOrWhiteSpace(table) || table.Length > MaxTableNameLength)
        {
            throw new HubException("Invalid table name.");
        }

        await using var connection = await _connections.OpenAsync(context.ProjectContext, Context.ConnectionAborted);

        try
        {
            // Apply the same request context the Runtime would use, so privileges are evaluated
            // against the anonymous or authenticated functional role.
            await ApplyRequestContextAsync(connection, null, context.ProjectContext, context.UserId);

            await using var command = new NpgsqlCommand(
                "SELECT c.relrowsecurity, has_table_privilege(c.oid, @privilege) " +
                "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = 'public' AND c.relname = @table AND c.relkind IN ('r', 'p')",
                connection);

            command.Parameters.AddWithValue("table", table);
            command.Parameters.AddWithValue("privilege", privilege);

            await using var reader = await command.ExecuteReaderAsync(Context.ConnectionAborted);
            if (!await reader.ReadAsync(Context.ConnectionAborted))
            {
                throw new HubException("Table is not exposed.");
            }

            var hasRls = reader.GetBoolean(0);
            var hasPrivilege = reader.GetBoolean(1);

            if (!hasRls)
            {
                throw new HubException("Table is not exposed.");
            }

            if (!hasPrivilege)
            {
                throw new HubException("Not authorized for this table.");
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Realtime authorization check failed for project {ProjectId}, table {Table}: {Message}",
                context.ProjectContext.ProjectId,
                table,
                exception.Message);
            throw new HubException($"Authorization check failed: {exception.Message}");
        }
        finally
        {
            try
            {
                await using var resetCommand = new NpgsqlCommand("RESET ROLE", connection);
                await resetCommand.ExecuteNonQueryAsync();
            }
            catch (Exception resetException)
            {
                _logger.LogDebug(resetException, "Could not reset role after realtime authorization check.");
            }
        }
    }

    private static async Task ApplyRequestContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimeProjectContext context,
        Guid? userId)
    {
        var isAuthenticated = userId.HasValue;
        var role = isAuthenticated ? context.AuthenticatedRole : context.AnonymousRole;

        if (!IsValidRoleIdentifier(role))
        {
            throw new InvalidOperationException("Invalid functional role identifier.");
        }

        // SET LOCAL ROLE is not parameterizable; the role name is generated by the server
        // and validated above against a strict identifier pattern.
        await using var roleCommand = new NpgsqlCommand(
            $"SET ROLE {role}",
            connection,
            transaction);
        await roleCommand.ExecuteNonQueryAsync();

        if (isAuthenticated)
        {
            await using var userCommand = new NpgsqlCommand(
                "SELECT set_config('domibase.user_id', @userId, true)",
                connection,
                transaction);
            userCommand.Parameters.AddWithValue("userId", userId!.Value.ToString());
            await userCommand.ExecuteScalarAsync();
        }
    }

    private static bool IsValidRoleIdentifier(string role)
    {
        if (string.IsNullOrEmpty(role) || role.Length > 63)
        {
            return false;
        }

        // Project-generated roles are lowercase ascii letters, digits and underscores,
        // starting with a letter.
        if (!char.IsAsciiLetter(role[0]))
        {
            return false;
        }

        for (var i = 1; i < role.Length; i++)
        {
            var c = role[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<AccessTokenClaims?> ValidateBearerAsync(
        RuntimeProjectContext context,
        string token,
        CancellationToken cancellationToken)
    {
        var keyMaterial = await _signingKeys.FindActiveAsync(context.ProjectId, cancellationToken);
        if (keyMaterial is null)
        {
            return null;
        }

        var validation = _accessTokens.Validate(keyMaterial, token);
        if (!validation.Succeeded || validation.Claims is null)
        {
            return null;
        }

        var claims = validation.Claims;
        if (claims.ProjectId != context.ProjectId ||
            claims.ApiKeyId != context.ApiKeyId ||
            !string.Equals(claims.Role, AccessTokenClaims.AuthenticatedRole, StringComparison.Ordinal))
        {
            return null;
        }

        return claims;
    }

    private static string? ReadApiKey(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("apikey", out var headerValues) &&
            !string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            return headerValues.ToString();
        }

        if (httpContext.Request.Query.TryGetValue(ApiKeyQueryName, out var queryValues) &&
            queryValues.Count > 0 &&
            !string.IsNullOrWhiteSpace(queryValues[0]))
        {
            return queryValues[0];
        }

        return null;
    }

    private static string? ReadBearer(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[prefix.Length..].Trim();
    }

    private static async Task RollbackQuietlyAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
        }
    }

    private sealed record RealtimeConnectionContext(RuntimeProjectContext ProjectContext, Guid? UserId);
}
