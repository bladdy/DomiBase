using DomiBase.Application.Security;

namespace DomiBase.UnitTests;

public class AdminTokenValidatorTests
{
    private const string Token = "test-admin-token-0123456789";

    [Fact]
    public void IsValid_AcceptsExactToken()
    {
        var validator = CreateValidator();

        Assert.True(validator.IsValid(Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token-0123456789")]
    [InlineData("test-admin-token-012345678")]
    [InlineData("test-admin-token-01234567890")]
    [InlineData("TEST-ADMIN-TOKEN-0123456789")]
    [InlineData(" test-admin-token-0123456789")]
    public void IsValid_RejectsWrongOrMissingToken(string? candidate)
    {
        var validator = CreateValidator();

        Assert.False(validator.IsValid(candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public void Constructor_RejectsMissingOrWeakToken(string token)
    {
        Assert.Throws<ArgumentException>(() => new AdminTokenValidator(new AdminTokenOptions { Token = token }));
    }

    private static AdminTokenValidator CreateValidator() =>
        new(new AdminTokenOptions { Token = Token });
}
