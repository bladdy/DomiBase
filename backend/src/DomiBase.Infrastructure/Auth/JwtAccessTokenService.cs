using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using DomiBase.Application.Auth;
using DomiBase.Infrastructure.Auth;
using Microsoft.IdentityModel.Tokens;

namespace DomiBase.Infrastructure.Auth;

/// <summary>
/// ES256 access tokens signed by the per-project ECDSA key.
/// Validation pins the algorithm, issuer, audience and lifetime; any failure is
/// reported as a generic <c>invalid_token</c> error without internal details.
/// </summary>
public sealed class JwtAccessTokenService(string issuer = "domibase", string audience = "domibase")
    : IAccessTokenService
{
    /// <summary>
    /// JwtSecurityTokenHandler is not thread-safe for signing/validation, so a fresh
    /// instance is created per operation.
    /// </summary>
    public const string ProjectClaim = "pid";
    public const string ApiKeyClaim = "akid";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private static CryptoProviderFactory CreateCryptoFactory() => new()
    {
        CacheSignatureProviders = false
    };

    private static JwtSecurityTokenHandler CreateHandler() => new()
    {
        // Keep compact claim names (sub, pid, akid, role) instead of mapping them
        // to the WS-* URI claim types on validation.
        MapInboundClaims = false
    };

    public IssuedAccessToken Issue(
        AuthSigningKeyMaterial keyMaterial,
        AccessTokenClaims claims,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        ArgumentException.ThrowIfNullOrEmpty(keyMaterial.PrivateKeyPkcs8Base64);
        ArgumentException.ThrowIfNullOrEmpty(keyMaterial.KeyId);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        using var privateKey = AuthKeyFactory.LoadPrivate(keyMaterial.PrivateKeyPkcs8Base64);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lifetime);

        var credentials = new SigningCredentials(
            new ECDsaSecurityKey(privateKey) { KeyId = keyMaterial.KeyId, CryptoProviderFactory = CreateCryptoFactory() },
            SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = CreateCryptoFactory()
        };

        var subject = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, claims.UserId.ToString()),
            new Claim(ProjectClaim, claims.ProjectId.ToString()),
            new Claim(ApiKeyClaim, claims.ApiKeyId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("role", claims.Role)
        ]);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = subject,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials
        };

        var handler = CreateHandler();
        var token = handler.WriteToken(handler.CreateToken(descriptor));
        return new IssuedAccessToken(token, expiresAt, keyMaterial.KeyId);
    }

    public AccessTokenValidationResult Validate(AuthSigningKeyMaterial keyMaterial, string token)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        ArgumentException.ThrowIfNullOrEmpty(keyMaterial.PublicKeySpkiBase64);

        if (string.IsNullOrWhiteSpace(token))
        {
            return AccessTokenValidationResult.Failure("invalid_token");
        }

        using var publicKey = AuthKeyFactory.LoadPublic(keyMaterial.PublicKeySpkiBase64);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new ECDsaSecurityKey(publicKey) { CryptoProviderFactory = CreateCryptoFactory() },
            CryptoProviderFactory = CreateCryptoFactory(),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = ClockSkew,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256]
        };

        ClaimsPrincipal principal;
        try
        {
            principal = CreateHandler().ValidateToken(token, parameters, out _);
        }
        catch (Exception exception) when (
            exception is SecurityTokenException or ArgumentException or FormatException)
        {
            return AccessTokenValidationResult.Failure("invalid_token");
        }

        if (!TryGetGuid(principal, JwtRegisteredClaimNames.Sub, out var userId) ||
            !TryGetGuid(principal, ProjectClaim, out var projectId) ||
            !TryGetGuid(principal, ApiKeyClaim, out var apiKeyId))
        {
            return AccessTokenValidationResult.Failure("invalid_claims");
        }

        var role = principal.FindFirst("role")?.Value;
        if (string.IsNullOrEmpty(role))
        {
            return AccessTokenValidationResult.Failure("invalid_claims");
        }

        return AccessTokenValidationResult.Success(
            new AccessTokenClaims(userId, projectId, apiKeyId, role));
    }

    private static bool TryGetGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        value = Guid.Empty;
        var claim = principal.FindFirst(claimType)?.Value;
        return claim is not null && Guid.TryParse(claim, out value);
    }
}
