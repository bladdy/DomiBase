using DomiBase.Application.Auth;

namespace DomiBase.IntegrationTests;

[Collection(AuthRepositoryCollection.Name)]
public sealed class AuthUserRepositoryTests(AuthRepositoryFixture fixture)
{
    [Fact]
    public async Task CreateUser_ThenFindByEmailAndId_ReturnsSameUser()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string hash = "$argon2id$v=19$m=65536,t=3,p=1$dummy$hash";

        var created = await fixture.Repository.CreateUserAsync(fixture.RuntimeContext, email, hash);
        Assert.NotNull(created);
        Assert.Equal(email.ToLowerInvariant(), created.Email);
        Assert.Equal(hash, created.PasswordHash);
        Assert.Null(created.DisabledAt);

        var byEmail = await fixture.Repository.FindByEmailAsync(fixture.RuntimeContext, email);
        Assert.NotNull(byEmail);
        Assert.Equal(created.Id, byEmail.Id);

        var byId = await fixture.Repository.FindByIdAsync(fixture.RuntimeContext, created.Id);
        Assert.NotNull(byId);
        Assert.Equal(created.Id, byId.Id);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_ReturnsNull()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        const string hash = "$argon2id$v=19$m=65536,t=3,p=1$dummy$hash";

        var first = await fixture.Repository.CreateUserAsync(fixture.RuntimeContext, email, hash);
        Assert.NotNull(first);

        var second = await fixture.Repository.CreateUserAsync(fixture.RuntimeContext, email, hash);
        Assert.Null(second);
    }

    [Fact]
    public async Task RefreshTokenLifecycle_CreateFindRotateRevoke_Works()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var user = await fixture.Repository.CreateUserAsync(
            fixture.RuntimeContext,
            email,
            "$argon2id$v=19$m=65536,t=3,p=1$dummy$hash");
        Assert.NotNull(user);

        var familyId = Guid.CreateVersion7();
        var token = RefreshToken.Generate();
        var tokenHash = RefreshToken.Hash(token);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);

        await fixture.Repository.CreateRefreshTokenAsync(
            fixture.RuntimeContext,
            user.Id,
            familyId,
            tokenHash,
            expiresAt);

        var found = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, tokenHash);
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.UserId);
        Assert.Equal(familyId, found.FamilyId);
        Assert.Null(found.ConsumedAt);
        Assert.Null(found.RevokedAt);

        var newToken = RefreshToken.Generate();
        var newHash = RefreshToken.Hash(newToken);
        var newExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var consumedAt = DateTimeOffset.UtcNow;

        var rotated = await fixture.Repository.RotateRefreshTokenAsync(
            fixture.RuntimeContext,
            found.Id,
            user.Id,
            familyId,
            newHash,
            consumedAt,
            newExpiresAt);
        Assert.True(rotated);

        var oldAfterRotation = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, tokenHash);
        Assert.NotNull(oldAfterRotation);
        Assert.NotNull(oldAfterRotation.ConsumedAt);

        var fresh = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, newHash);
        Assert.NotNull(fresh);
        Assert.Null(fresh.ConsumedAt);
        Assert.Null(fresh.RevokedAt);

        await fixture.Repository.RevokeFamilyAsync(fixture.RuntimeContext, familyId, DateTimeOffset.UtcNow);

        fresh = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, newHash);
        Assert.NotNull(fresh);
        Assert.NotNull(fresh.RevokedAt);
    }

    [Fact]
    public async Task RotateRefreshToken_AlreadyConsumed_ReturnsFalse()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var user = await fixture.Repository.CreateUserAsync(
            fixture.RuntimeContext,
            email,
            "$argon2id$v=19$m=65536,t=3,p=1$dummy$hash");
        Assert.NotNull(user);

        var familyId = Guid.CreateVersion7();
        var token = RefreshToken.Generate();
        var tokenHash = RefreshToken.Hash(token);

        await fixture.Repository.CreateRefreshTokenAsync(
            fixture.RuntimeContext,
            user.Id,
            familyId,
            tokenHash,
            DateTimeOffset.UtcNow.AddDays(7));

        var found = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, tokenHash);
        Assert.NotNull(found);

        var rotatedOnce = await fixture.Repository.RotateRefreshTokenAsync(
            fixture.RuntimeContext,
            found.Id,
            user.Id,
            familyId,
            RefreshToken.Hash(RefreshToken.Generate()),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7));
        Assert.True(rotatedOnce);

        var rotatedTwice = await fixture.Repository.RotateRefreshTokenAsync(
            fixture.RuntimeContext,
            found.Id,
            user.Id,
            familyId,
            RefreshToken.Hash(RefreshToken.Generate()),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7));
        Assert.False(rotatedTwice);
    }

    [Fact]
    public async Task RevokeRefreshToken_ByHash_MarksTokenRevoked()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var user = await fixture.Repository.CreateUserAsync(
            fixture.RuntimeContext,
            email,
            "$argon2id$v=19$m=65536,t=3,p=1$dummy$hash");
        Assert.NotNull(user);

        var token = RefreshToken.Generate();
        var tokenHash = RefreshToken.Hash(token);

        await fixture.Repository.CreateRefreshTokenAsync(
            fixture.RuntimeContext,
            user.Id,
            Guid.CreateVersion7(),
            tokenHash,
            DateTimeOffset.UtcNow.AddDays(7));

        await fixture.Repository.RevokeRefreshTokenAsync(
            fixture.RuntimeContext,
            tokenHash,
            DateTimeOffset.UtcNow);

        var found = await fixture.Repository.FindRefreshTokenAsync(fixture.RuntimeContext, tokenHash);
        Assert.NotNull(found);
        Assert.NotNull(found.RevokedAt);
    }
}
