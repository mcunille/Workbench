// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Workbench.Server.Authorization;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SessionAuthenticationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private SqlTestDatabase _database = null!;
    private SessionService _sessions = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
        var webConnection = await _database.CreateWebUserAsync();
        await SeedUserAsync();
        _sessions = new SessionService(webConnection, new SessionOptions());
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateStoresOnlyTheHashOfTheRandomToken()
    {
        var created = await _sessions.CreateAsync(
            new VerifiedIdentity(BuiltInPasswordVerifier.Scheme, UserId.ToString("N"), UserId, TenantId),
            Now,
            CancellationToken.None);

        var storedHash = await ReadStoredHashAsync(created.Id);

        Assert.Equal(SessionToken.Hash(created.Token), storedHash);
        Assert.NotEqual(created.Token, Convert.ToBase64String(storedHash));
        Assert.Equal(32, storedHash.Length);
    }

    [Fact]
    public async Task ResolveReloadsCurrentDurableAccountAndTenantState()
    {
        var created = await CreateSessionAsync();

        var resolved = await _sessions.ResolveAsync(created.Token, Now.AddMinutes(1), CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(UserId, resolved.UserId);
        Assert.Equal(TenantId, resolved.TenantId);
        Assert.Equal(created.Id, resolved.SessionId);
        Assert.Contains(WorkbenchPermissions.TenantAccess, resolved.Permissions);
    }

    [Fact]
    public async Task ResolveRejectsAChangedAccountSecurityVersion()
    {
        var created = await CreateSessionAsync();
        await ExecuteAdminAsync("UPDATE [Identity].[Users] SET [SecurityVersion] = 2 WHERE [Id] = @id", UserId);

        var resolved = await _sessions.ResolveAsync(created.Token, Now.AddMinutes(1), CancellationToken.None);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("UPDATE [Identity].[Users] SET [State] = 2 WHERE [Id] = @id")]
    [InlineData("UPDATE [Tenancy].[Tenants] SET [IsEnabled] = 0 WHERE [Id] = @id")]
    public async Task ResolveRejectsDisabledAuthority(string disableSql)
    {
        var created = await CreateSessionAsync();
        await ExecuteAdminAsync(disableSql, disableSql.Contains("Users", StringComparison.Ordinal) ? UserId : TenantId);

        var resolved = await _sessions.ResolveAsync(created.Token, Now.AddMinutes(1), CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveTreatsMalformedTokenAsUnauthenticated()
    {
        Assert.Null(await _sessions.ResolveAsync("not a base64url token!", Now, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRejectsDisabledTenant()
    {
        await ExecuteAdminAsync(
            "UPDATE [Tenancy].[Tenants] SET [IsEnabled] = 0 WHERE [Id] = @id",
            TenantId);

        await Assert.ThrowsAsync<InvalidOperationException>(CreateSessionAsync);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(721)]
    public async Task ResolveRejectsExpiredSession(int elapsedMinutes)
    {
        var created = await CreateSessionAsync();

        var resolved = await _sessions.ResolveAsync(
            created.Token,
            Now.AddMinutes(elapsedMinutes),
            CancellationToken.None);

        Assert.Null(resolved);
    }

    private Task<CreatedSession> CreateSessionAsync() =>
        _sessions.CreateAsync(
            new VerifiedIdentity(BuiltInPasswordVerifier.Scheme, UserId.ToString("N"), UserId, TenantId),
            Now,
            CancellationToken.None);

    private async Task SeedUserAsync()
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
            CreatedAtUtc = Now,
        });
        database.Users.Add(new WorkbenchUser
        {
            Id = UserId,
            TenantId = TenantId,
            UserName = "admin@example.com",
            NormalizedUserName = "ADMIN@EXAMPLE.COM",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            PasswordHash = new PasswordHasher<WorkbenchUser>().HashPassword(new WorkbenchUser(), "unused"),
            CreatedAtUtc = Now,
        });
        var roleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        database.Roles.Add(new WorkbenchRole
        {
            Id = roleId,
            TenantId = TenantId,
            Name = "Tenant member",
            NormalizedName = "TENANT MEMBER",
        });
        database.Set<WorkbenchUserRole>().Add(new WorkbenchUserRole
        {
            TenantId = TenantId,
            UserId = UserId,
            RoleId = roleId,
        });
        database.Set<WorkbenchRoleClaim>().Add(new WorkbenchRoleClaim
        {
            TenantId = TenantId,
            RoleId = roleId,
            ClaimType = SessionCookieHandler.PermissionClaimType,
            ClaimValue = WorkbenchPermissions.TenantAccess,
        });
        await database.SaveChangesAsync();
    }

    private async Task<byte[]> ReadStoredHashAsync(Guid sessionId)
    {
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [TokenHash] FROM [Identity].[Sessions] WHERE [Id] = @id",
            connection);
        command.Parameters.AddWithValue("@id", sessionId);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql, Guid id)
    {
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}
