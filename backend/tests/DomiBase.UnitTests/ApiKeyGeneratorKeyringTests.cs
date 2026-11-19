using DomiBase.Application.ApiKeys;
using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

public class ApiKeyGeneratorKeyringTests
{
    private const string OldPepper = "retired-pepper-0123456789";
    private const string NewPepper = "active-pepper-0123456789";

    [Fact]
    public void ComputeHash_UsesActivePepper()
    {
        var generator = CreateGenerator();
        const string value = "db_public_example-value";

        Assert.Equal(generator.ComputeHash(value), new ApiKeyGenerator(new ApiKeyGeneratorOptions
        {
            Pepper = NewPepper
        }).ComputeHash(value));
        Assert.NotEqual(generator.ComputeHash(value), new ApiKeyGenerator(new ApiKeyGeneratorOptions
        {
            Pepper = OldPepper
        }).ComputeHash(value));
    }

    [Fact]
    public void Verify_AcceptsHashesProducedByEveryPepperInTheKeyring()
    {
        var generator = CreateGenerator();
        var oldGenerator = new ApiKeyGenerator(new ApiKeyGeneratorOptions { Pepper = OldPepper });
        var generated = oldGenerator.Generate(ApiKeyKind.Public);

        Assert.Contains(generated.SecretHash, generator.ComputeVerificationHashes(generated.Value));
        Assert.True(generator.Verify(generated.Value, generated.SecretHash));
        Assert.False(generator.Verify(generated.Value + "x", generated.SecretHash));
        Assert.False(generator.Verify(generated.Value, "not-a-hash"));
    }

    [Fact]
    public void GeneratedKeys_AreVerifiableAfterPepperRotation()
    {
        var oldGenerator = new ApiKeyGenerator(new ApiKeyGeneratorOptions { Pepper = OldPepper });
        var generated = oldGenerator.Generate(ApiKeyKind.Public);

        var rotated = CreateGenerator();

        Assert.True(rotated.Verify(generated.Value, generated.SecretHash));

        var fresh = rotated.Generate(ApiKeyKind.Public);
        Assert.False(oldGenerator.Verify(fresh.Value, fresh.SecretHash));
        Assert.True(rotated.Verify(fresh.Value, fresh.SecretHash));
    }

    [Fact]
    public void Constructor_RejectsWeakAndDuplicatePeppers()
    {
        Assert.Throws<ArgumentException>(() => new ApiKeyGenerator(new ApiKeyGeneratorOptions
        {
            Pepper = NewPepper,
            AdditionalPeppers = ["short"]
        }));

        Assert.Throws<ArgumentException>(() => new ApiKeyGenerator(new ApiKeyGeneratorOptions
        {
            Pepper = NewPepper,
            AdditionalPeppers = [NewPepper]
        }));
    }

    private static ApiKeyGenerator CreateGenerator() => new(new ApiKeyGeneratorOptions
    {
        Pepper = NewPepper,
        AdditionalPeppers = [OldPepper]
    });
}
