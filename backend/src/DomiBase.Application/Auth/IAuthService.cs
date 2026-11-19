using DomiBase.Application.Runtime;

namespace DomiBase.Application.Auth;

/// <summary>
/// Project-scoped authentication operations. Implementations resolve all secrets server-side;
/// callers only supply the public API key context and user credentials.
/// </summary>
public interface IAuthService
{
    Task<AuthResult> RegisterAsync(
        RuntimeProjectContext context,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task<AuthResult> LoginAsync(
        RuntimeProjectContext context,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task<AuthResult> RefreshAsync(
        RuntimeProjectContext context,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<AuthResult> LogoutAsync(
        RuntimeProjectContext context,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
