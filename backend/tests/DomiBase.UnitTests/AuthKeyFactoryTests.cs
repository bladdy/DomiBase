using System.Security.Cryptography;
using DomiBase.Infrastructure.Auth;
using Xunit;

namespace DomiBase.UnitTests;

public class AuthKeyFactoryTests
{
    [Fact]
    public void Generate_ProducesStableKeyIdAndReloadableKeys()
    {
        var pair = AuthKeyFactory.Generate();
        Assert.Equal(16, pair.KeyId.Length);

        using var publicKey = AuthKeyFactory.LoadPublic(pair.PublicKeySpkiBase64);
        using var privateKey = AuthKeyFactory.LoadPrivate(pair.PrivateKeyPkcs8Base64);

        var data = "domibase"u8.ToArray();
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256);

        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Generate_ProducesUniquePairs()
    {
        var first = AuthKeyFactory.Generate();
        var second = AuthKeyFactory.Generate();

        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.NotEqual(first.PrivateKeyPkcs8Base64, second.PrivateKeyPkcs8Base64);
    }

    [Fact]
    public void ComputeKeyId_IsDeterministic()
    {
        var pair = AuthKeyFactory.Generate();
        var spki = Convert.FromBase64String(pair.PublicKeySpkiBase64);

        Assert.Equal(pair.KeyId, AuthKeyFactory.ComputeKeyId(spki));
    }
}
