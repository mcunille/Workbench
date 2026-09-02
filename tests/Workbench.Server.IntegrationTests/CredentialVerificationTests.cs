// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class CredentialVerificationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DisabledUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Email = "admin@example.com";
    private const string Password = "Correct-Horse-Battery-Staple-47!";
    private SqlTestDatabase _database = null!;
    private string _webConnectionString = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
        _webConnectionString = await _database.CreateWebUserAsync();
        await SeedUsersAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task CorrectBuiltInPasswordResolvesOneLocalUserAndTenant()
    {
        var verifier = CreateVerifier();

        var identity = await verifier.VerifyAsync(Email, Password, CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(BuiltInPasswordVerifier.Scheme, identity.Scheme);
        Assert.Equal(UserId.ToString("N"), identity.Subject);
        Assert.Equal(UserId, identity.UserId);
        Assert.Equal(TenantId, identity.TenantId);
    }

    [Theory]
    [InlineData(Email, "wrong-password")]
    [InlineData("missing@example.com", Password)]
    public async Task InvalidCredentialsReturnOneIndistinguishableFailure(string email, string password)
    {
        var verifier = CreateVerifier();

        var identity = await verifier.VerifyAsync(email, password, CancellationToken.None);

        Assert.Null(identity);
    }

    [Fact]
    public async Task DisabledAccountUsesTheSameAuthenticationFailure()
    {
        var verifier = CreateVerifier();

        var identity = await verifier.VerifyAsync(
            "disabled@example.com",
            Password,
            CancellationToken.None);

        Assert.Null(identity);
    }

    private BuiltInPasswordVerifier CreateVerifier() =>
        new(_webConnectionString, new PasswordHasher<WorkbenchUser>());

    private async Task SeedUsersAsync()
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(_database.AdminConnectionString)
            .Options;
        await using var database = new WorkbenchDbContext(options, TenantContext.None);

        database.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Tenant A",
            NormalizedName = "TENANT A",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var user = CreateUser(UserId, Email, AccountState.Enabled);
        var disabledUser = CreateUser(
            DisabledUserId,
            "disabled@example.com",
            AccountState.Disabled);
        database.Users.AddRange(user, disabledUser);
        database.LoginDirectory.AddRange(
            CreateDirectoryEntry(user),
            CreateDirectoryEntry(disabledUser));

        await database.SaveChangesAsync();
    }

    private static WorkbenchUser CreateUser(Guid userId, string email, AccountState state)
    {
        var user = new WorkbenchUser
        {
            Id = userId,
            TenantId = TenantId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            State = state,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<WorkbenchUser>().HashPassword(user, Password);
        return user;
    }

    private static LoginDirectoryEntry CreateDirectoryEntry(WorkbenchUser user) =>
        new()
        {
            NormalizedEmail = user.NormalizedEmail!,
            UserId = user.Id,
            TenantId = user.TenantId,
        };
}
