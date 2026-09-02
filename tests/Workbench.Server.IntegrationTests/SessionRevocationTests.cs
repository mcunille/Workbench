// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SessionRevocationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private SqlTestDatabase _database = null!;
    private SessionService _firstReplica = null!;
    private SessionService _secondReplica = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
        var webConnection = await _database.CreateWebUserAsync();
        var tenantContextProof = new TenantContextProof(await _database.GetTenantContextProofKeyAsync());
        await SeedUserAsync();
        _firstReplica = new SessionService(webConnection, new SessionOptions(), tenantContextProof);
        _secondReplica = new SessionService(webConnection, new SessionOptions(), tenantContextProof);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task RevocationByOneReplicaIsImmediateOnAnotherReplica()
    {
        var created = await CreateSessionAsync();

        await _firstReplica.RevokeAsync(TenantId, created.Id, "UserSignOut", Now.AddMinutes(1), CancellationToken.None);

        Assert.Null(await _secondReplica.ResolveAsync(created.Token, Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task RevokeAllInvalidatesEveryUserSession()
    {
        var first = await CreateSessionAsync();
        var second = await CreateSessionAsync();

        await _firstReplica.RevokeAllAsync(TenantId, UserId, "PasswordChanged", Now.AddMinutes(1), CancellationToken.None);

        Assert.Null(await _secondReplica.ResolveAsync(first.Token, Now.AddMinutes(2), CancellationToken.None));
        Assert.Null(await _secondReplica.ResolveAsync(second.Token, Now.AddMinutes(2), CancellationToken.None));
    }

    private Task<CreatedSession> CreateSessionAsync() =>
        _firstReplica.CreateAsync(
            new VerifiedIdentity(BuiltInPasswordVerifier.Scheme, UserId.ToString("N"), UserId, TenantId),
            Now,
            CancellationToken.None);

    private async Task SeedUserAsync()
    {
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO [Tenancy].[Tenants]
                ([Id], [Name], [NormalizedName], [IsEnabled], [CreatedAtUtc])
            VALUES
                (@tenantId, N'Tenant A', N'TENANT A', 1, @now);

            INSERT INTO [Identity].[Users]
                ([Id], [TenantId], [SecurityVersion], [State], [CreatedAtUtc],
                 [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                 [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
            VALUES
                (@userId, @tenantId, 1, 1, @now,
                 N'admin@example.com', N'ADMIN@EXAMPLE.COM', N'admin@example.com', N'ADMIN@EXAMPLE.COM', 1,
                 0, 0, 0, 0);
            """, connection);
        command.Parameters.AddWithValue("@tenantId", TenantId);
        command.Parameters.AddWithValue("@userId", UserId);
        command.Parameters.AddWithValue("@now", Now);
        await command.ExecuteNonQueryAsync();
    }
}
