using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DomiBase.Application.Auth;
using DomiBase.Infrastructure.Auth;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DomiBase.UnitTests;

public class JwtAccessTokenServiceTests
{
    private readonly JwtAccessTokenService _service = new();
    private readonly AuthKeyPair _pair = AuthKeyFactory.Generate();

    private AuthSigningKeyMaterial KeyMaterial => new(_pair.KeyId, _pair.PublicKeySpkiBase64, _pair.PrivateKeyPkcs8Base64);

    [Fact]
    public void IssueAndValidate_ReturnsSignedClaims()
    {
        var claims = NewClaims();

        var issued = _service.Issue(KeyMaterial, claims, TimeSpan.FromMinutes(15));

        Assert.Equal(_pair.KeyId, issued.KeyId);
        Assert.True(issued.ExpiresAt > DateTimeOffset.UtcNow);

        var validated = _service.Validate(KeyMaterial, issued.Token);

        Assert.True(validated.Succeeded);
        Assert.Equal(claims, validated.Claims);
    }

    [Fact]
    public void Issue_WithRoundTrippedKeyMaterial_Works()
    {
        // Simulate storage encryption roundtrip (AesGcmSecretProtector uses UTF-8).
        var protector = new Infrastructure.Security.AesGcmSecretProtector(
            new Infrastructure.Security.SecretProtectionOptions
            {
                Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()
            });

        var protectedKey = protector.Protect(_pair.PrivateKeyPkcs8Base64);
        Assert.True(protector.TryUnprotect(protectedKey, out var decryptedKey));

        var roundTrippedMaterial = new AuthSigningKeyMaterial(_pair.KeyId, _pair.PublicKeySpkiBase64, decryptedKey);

        var issued = _service.Issue(roundTrippedMaterial, NewClaims(), TimeSpan.FromMinutes(5));
        Assert.False(string.IsNullOrWhiteSpace(issued.Token));

        var validated = _service.Validate(roundTrippedMaterial, issued.Token);
        Assert.True(validated.Succeeded);
    }

    [Fact]
    public void Issue_TwiceWithSameKey_Works()
    {
        var claims = NewClaims();
        var secondMaterial = new AuthSigningKeyMaterial(_pair.KeyId, _pair.PublicKeySpkiBase64, _pair.PrivateKeyPkcs8Base64);

        var first = _service.Issue(KeyMaterial, claims, TimeSpan.FromMinutes(5));
        var second = _service.Issue(secondMaterial, claims, TimeSpan.FromMinutes(5));

        Assert.False(string.IsNullOrWhiteSpace(first.Token));
        Assert.False(string.IsNullOrWhiteSpace(second.Token));
        Assert.NotEqual(first.Token, second.Token);

        Assert.True(_service.Validate(KeyMaterial, first.Token).Succeeded);
        Assert.True(_service.Validate(secondMaterial, second.Token).Succeeded);
    }

    [Fact]
    public void Validate_RejectsTokenSignedByAnotherKey()
    {
        var other = AuthKeyFactory.Generate();
        var otherMaterial = new AuthSigningKeyMaterial(other.KeyId, other.PublicKeySpkiBase64, other.PrivateKeyPkcs8Base64);

        var issued = _service.Issue(otherMaterial, NewClaims(), TimeSpan.FromMinutes(5));
        var validated = _service.Validate(KeyMaterial, issued.Token);

        Assert.False(validated.Succeeded);
        Assert.Null(validated.Claims);
        Assert.Equal("invalid_token", validated.Error);
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var token = CreateToken(expiresAt: DateTime.UtcNow.AddHours(-2));
        var validated = _service.Validate(KeyMaterial, token);

        Assert.False(validated.Succeeded);
    }

    [Fact]
    public void Validate_RejectsWrongAudience()
    {
        var issued = _service.Issue(KeyMaterial, NewClaims(), TimeSpan.FromMinutes(5));
        var otherAudience = new JwtAccessTokenService(audience: "other");

        var validated = otherAudience.Validate(KeyMaterial, issued.Token);

        Assert.False(validated.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("aaa.bbb.ccc")]
    public void Validate_RejectsMalformedTokens(string token)
    {
        var validated = _service.Validate(KeyMaterial, token);
        Assert.False(validated.Succeeded);
    }

    private string CreateToken(DateTime expiresAt)
    {
        var handler = new JwtSecurityTokenHandler();
        using var privateKey = AuthKeyFactory.LoadPrivate(_pair.PrivateKeyPkcs8Base64);
        var credentials = new SigningCredentials(
            new ECDsaSecurityKey(privateKey) { KeyId = _pair.KeyId },
            SecurityAlgorithms.EcdsaSha256);

        var claims = NewClaims();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "domibase",
            Audience = "domibase",
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, claims.UserId.ToString()),
                new Claim(JwtAccessTokenService.ProjectClaim, claims.ProjectId.ToString()),
                new Claim(JwtAccessTokenService.ApiKeyClaim, claims.ApiKeyId.ToString()),
                new Claim("role", claims.Role)
            ]),
            IssuedAt = expiresAt.AddHours(-1),
            NotBefore = expiresAt.AddHours(-1),
            Expires = expiresAt,
            SigningCredentials = credentials
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static AccessTokenClaims NewClaims() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        AccessTokenClaims.AuthenticatedRole);
}
