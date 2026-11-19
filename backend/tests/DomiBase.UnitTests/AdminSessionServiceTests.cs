using DomiBase.Application.Security;

namespace DomiBase.UnitTests;

public class AdminSessionServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TrySignIn_IssuesValidSessionForConfiguredCredentials()
    {
        var time = new FixedTimeProvider(Start);
        var service = CreateService(time);

        var result = service.TrySignIn("owner", "owner-password-0123456789", out var token, out var expiresAt);

        Assert.True(result);
        Assert.StartsWith("dbsess_", token, StringComparison.Ordinal);
        Assert.Equal(Start.AddHours(1), expiresAt);
        Assert.True(service.IsValid(token));
    }

    [Theory]
    [InlineData("wrong", "owner-password-0123456789")]
    [InlineData("owner", "wrong-password-0123456789")]
    [InlineData(null, "owner-password-0123456789")]
    [InlineData("owner", null)]
    public void TrySignIn_RejectsInvalidCredentials(string? username, string? password)
    {
        var service = CreateService();

        var result = service.TrySignIn(username, password, out var token, out _);

        Assert.False(result);
        Assert.Equal(string.Empty, token);
    }

    [Fact]
    public void IsValid_RejectsTamperedToken()
    {
        var service = CreateService();
        Assert.True(service.TrySignIn("owner", "owner-password-0123456789", out var token, out _));

        var body = token["dbsess_".Length..];
        var dot = body.IndexOf('.');
        var payload = body[..dot];
        var signature = body[(dot + 1)..];
        var tamperedPayload = payload[..^1] + (payload[^1] == 'A' ? 'B' : 'A');

        var tamperedSignature = signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A');

        Assert.False(service.IsValid("dbsess_" + tamperedPayload + "." + signature));
        Assert.False(service.IsValid("dbsess_" + payload + "." + tamperedSignature));
    }

    [Fact]
    public void IsValid_RejectsExpiredSession()
    {
        var time = new FixedTimeProvider(Start);
        var service = CreateService(time);
        Assert.True(service.TrySignIn("owner", "owner-password-0123456789", out var token, out _));
        Assert.True(service.IsValid(token));

        time.Now = Start.AddHours(1).AddSeconds(1);

        Assert.False(service.IsValid(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token")]
    [InlineData("dbsess_")]
    [InlineData("dbsess_abc")]
    [InlineData("dbsess_abc.def.ghi")]
    [InlineData("dbsess_abc!.def")]
    public void IsValid_RejectsMalformedToken(string? token)
    {
        Assert.False(CreateService().IsValid(token));
    }

    [Fact]
    public void Constructor_RejectsWeakCredentialSettings()
    {
        Assert.Throws<ArgumentException>(() => new AdminSessionService(new AdminLoginOptions
        {
            Username = "owner",
            Password = "short",
            SigningSecret = "session-signing-secret-0123456789abcdef"
        }));

        Assert.Throws<ArgumentException>(() => new AdminSessionService(new AdminLoginOptions
        {
            Username = "owner",
            Password = "owner-password-0123456789",
            SigningSecret = "short"
        }));
    }

    private static AdminSessionService CreateService(TimeProvider? time = null) =>
        new(new AdminLoginOptions
        {
            Username = "owner",
            Password = "owner-password-0123456789",
            SigningSecret = "session-signing-secret-0123456789abcdef",
            Lifetime = TimeSpan.FromHours(1)
        }, time);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
