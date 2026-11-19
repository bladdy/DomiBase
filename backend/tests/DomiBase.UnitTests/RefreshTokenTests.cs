using DomiBase.Application.Auth;
using Xunit;

namespace DomiBase.UnitTests;

public class RefreshTokenTests
{
    [Fact]
    public void Generate_Produces43CharacterBase64UrlTokens()
    {
        var token = RefreshToken.Generate();

        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.NotEqual(RefreshToken.Generate(), token);
    }

    [Fact]
    public void Hash_IsDeterministicLowercaseHex()
    {
        var token = RefreshToken.Generate();
        var hash = RefreshToken.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
        Assert.Equal(hash, RefreshToken.Hash(token));
    }

    [Fact]
    public void Hash_RejectsEmptyToken()
    {
        Assert.ThrowsAny<ArgumentException>(() => RefreshToken.Hash(""));
    }
}
