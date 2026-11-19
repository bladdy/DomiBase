using DomiBase.Infrastructure.Auth;
using Xunit;

namespace DomiBase.UnitTests;

public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void HashAndVerify_Roundtrips()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash);
        Assert.True(_hasher.Verify(hash, "correct horse battery staple"));
        Assert.False(_hasher.Verify(hash, "wrong password"));
    }

    [Fact]
    public void Hash_UsesDistinctSalts()
    {
        var first = _hasher.Hash("same-password");
        var second = _hasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.True(_hasher.Verify(first, "same-password"));
        Assert.True(_hasher.Verify(second, "same-password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-phc-hash")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$short$short")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=1024,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Verify_RejectsMalformedOrUnexpectedHashes(string stored)
    {
        Assert.False(_hasher.Verify(stored, "password"));
    }

    [Fact]
    public void Verify_RejectsEmptyPassword()
    {
        var hash = _hasher.Hash("password");
        Assert.False(_hasher.Verify(hash, ""));
    }

    [Fact]
    public void Hash_RejectsEmptyPassword()
    {
        Assert.ThrowsAny<ArgumentException>(() => _hasher.Hash(""));
    }
}
