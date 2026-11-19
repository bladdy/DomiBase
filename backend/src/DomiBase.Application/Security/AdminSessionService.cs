using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DomiBase.Application.Security;

/// <summary>
/// Authenticates the single owner with a configured username/password and issues
/// a short-lived, HMAC-signed session token for the private admin panel.
/// The permanent administrative token is never returned to the browser.
/// </summary>
public sealed class AdminSessionService
{
    private const string TokenPrefix = "dbsess_";

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly AdminLoginOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _usernameHash;
    private readonly byte[] _passwordHash;
    private readonly byte[] _signingKey;

    public AdminSessionService(AdminLoginOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new ArgumentException("Admin username must be configured.", nameof(options));
        }

        if (options.Password.Length < AdminLoginOptions.MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Admin password must be at least {AdminLoginOptions.MinimumPasswordLength} characters long.",
                nameof(options));
        }

        if (options.SigningSecret.Length < AdminLoginOptions.MinimumSecretLength)
        {
            throw new ArgumentException(
                $"Admin session secret must be at least {AdminLoginOptions.MinimumSecretLength} characters long.",
                nameof(options));
        }

        if (options.Lifetime < AdminLoginOptions.MinimumLifetime ||
            options.Lifetime > AdminLoginOptions.MaximumLifetime)
        {
            throw new ArgumentException("Admin session lifetime is outside the accepted range.", nameof(options));
        }

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _usernameHash = Hash(options.Username);
        _passwordHash = Hash(options.Password);
        _signingKey = Encoding.UTF8.GetBytes(options.SigningSecret);
    }

    /// <summary>
    /// Verifies the configured credentials in constant time and issues a signed session token.
    /// </summary>
    public bool TrySignIn(string? username, string? password, out string token, out DateTimeOffset expiresAt)
    {
        token = string.Empty;
        expiresAt = default;

        if (username is null || password is null)
        {
            return false;
        }

        var usernameMatches = CryptographicOperations.FixedTimeEquals(Hash(username), _usernameHash);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(Hash(password), _passwordHash);
        if (!usernameMatches || !passwordMatches)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        expiresAt = now.Add(_options.Lifetime);

        var payload = new SessionPayload(_options.Username, now.ToUnixTimeSeconds(), expiresAt.ToUnixTimeSeconds());
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadOptions);
        var signature = HMACSHA256.HashData(_signingKey, payloadBytes);

        token = TokenPrefix + Encode(payloadBytes) + "." + Encode(signature);
        return true;
    }

    /// <summary>Validates signature, subject and expiry without any database or external state.</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = token[TokenPrefix.Length..].Split('.');
        if (parts.Length != 2 ||
            !TryDecode(parts[0], out var payloadBytes) ||
            !TryDecode(parts[1], out var signature))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_signingKey, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return false;
        }

        SessionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SessionPayload>(payloadBytes, PayloadOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null ||
            !string.Equals(payload.Sub, _options.Username, StringComparison.Ordinal))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return payload.Exp > now && payload.Iat <= now;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value.Length == 0 || value.Contains('+') || value.Contains('/') || value.Contains('='))
        {
            return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += (value.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => string.Empty
            };
            if (value.Length % 4 == 1)
            {
                return false;
            }

            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record SessionPayload(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}
