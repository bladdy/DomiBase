using DomiBase.Application.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.IntegrationTests;

[Collection(AuthRepositoryCollection.Name)]
public sealed class AuthServiceDirectTests(AuthRepositoryFixture fixture)
{
    [Fact]
    public async Task Register_ThenLogin_Directly_Works()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var email = $"{Guid.NewGuid():N}@example.com";
        const string password = "secure-password-123";

        var register = await auth.RegisterAsync(fixture.RuntimeContext, email, password);
        Assert.Equal(AuthError.None, register.Error);
        Assert.NotNull(register.Session);

        var login = await auth.LoginAsync(fixture.RuntimeContext, email, password);
        Assert.Equal(AuthError.None, login.Error);
        Assert.NotNull(login.Session);
    }
}
