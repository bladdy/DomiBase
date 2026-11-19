using DomiBase.Application.ApiKeys;
using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

public class ApiKeyGeneratorTests
{
    private const string Pepper = "unit-test-pepper-0123456789";

    [Fact]
    public void Generate_PublicKey_HasPrefixEntropyAndVerifiableHash()
    {
        var generator = CreateGenerator();

        var generated = generator.Generate(ApiKeyKind.Public);

        Assert.StartsWith("db_public_", generated.Value);
        Assert.StartsWith("db_public_", generated.Prefix);
        Assert.Equal(18, generated.Prefix.Length);
        Assert.True(generated.Value.Length > 30);
        Assert.Equal(64, generated.SecretHash.Length);
        Assert.Equal(generated.SecretHash, generator.ComputeHash(generated.Value));
        Assert.NotEqual(generated.Value, generated.SecretHash);
    }

    [Fact]
    public void Generate_SecretKey_UsesSecretPrefix()
    {
        var generated = CreateGenerator().Generate(ApiKeyKind.Secret);

        Assert.StartsWith("db_secret_", generated.Value);
    }

    [Fact]
    public void Generate_ProducesUniqueValues()
    {
        var generator = CreateGenerator();

        var first = generator.Generate(ApiKeyKind.Public);
        var second = generator.Generate(ApiKeyKind.Public);

        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.SecretHash, second.SecretHash);
    }

    [Fact]
    public void ComputeHash_IsDeterministicForSamePepper()
    {
        var value = "db_public_example-value";

        Assert.Equal(CreateGenerator().ComputeHash(value), CreateGenerator().ComputeHash(value));
    }

    [Fact]
    public void ComputeHash_DependsOnPepper()
    {
        var value = "db_public_example-value";
        var otherGenerator = new ApiKeyGenerator(new ApiKeyGeneratorOptions { Pepper = "another-pepper-0123456789" });

        Assert.NotEqual(CreateGenerator().ComputeHash(value), otherGenerator.ComputeHash(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Constructor_RejectsWeakPepper(string pepper)
    {
        Assert.Throws<ArgumentException>(() => new ApiKeyGenerator(new ApiKeyGeneratorOptions { Pepper = pepper }));
    }

    private static ApiKeyGenerator CreateGenerator() =>
        new(new ApiKeyGeneratorOptions { Pepper = Pepper });
}
