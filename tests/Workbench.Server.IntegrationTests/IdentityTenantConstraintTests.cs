// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class IdentityTenantConstraintTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SqlTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task UserCannotReceiveRoleFromAnotherTenant()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(_database.AdminConnectionString)
            .Options;
        await using var database = new WorkbenchDbContext(options, TenantContext.None);

        database.Tenants.AddRange(
            CreateTenant(tenantA, "A"),
            CreateTenant(tenantB, "B"));
        var user = new WorkbenchUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantA,
            UserName = "member@example.com",
            NormalizedUserName = "MEMBER@EXAMPLE.COM",
            Email = "member@example.com",
            NormalizedEmail = "MEMBER@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var role = new WorkbenchRole
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantB,
            Name = "TenantAdministrator",
            NormalizedName = "TENANTADMINISTRATOR",
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };
        database.Users.Add(user);
        database.Roles.Add(role);
        await database.SaveChangesAsync();

        database.UserRoles.Add(new WorkbenchUserRole
        {
            UserId = user.Id,
            RoleId = role.Id,
            TenantId = tenantA,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task IdentityRlsStillAppliesWhenEfFiltersAreDisabled()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await SeedUsersForBothTenantsAsync(tenantA, tenantB);
        var webConnection = await _database.CreateWebUserAsync();
        var tenantContext = new TenantContext(tenantA);
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(webConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenantContext))
            .Options;
        await using var database = new WorkbenchDbContext(options, tenantContext);

        var visibleUsers = await database.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync();

        var user = Assert.Single(visibleUsers);
        Assert.Equal(tenantA, user.TenantId);
    }

    [Fact]
    public async Task WebPrincipalCannotEnumerateTheLoginDirectory()
    {
        var webConnection = await _database.CreateWebUserAsync();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(webConnection);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM [Identity].[LoginDirectory]",
            connection);

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => command.ExecuteScalarAsync());
    }

    private async Task SeedUsersForBothTenantsAsync(Guid tenantA, Guid tenantB)
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(_database.AdminConnectionString)
            .Options;
        await using var database = new WorkbenchDbContext(options, TenantContext.None);
        database.Tenants.AddRange(CreateTenant(tenantA, "A"), CreateTenant(tenantB, "B"));
        database.Users.AddRange(CreateUser(tenantA, "a@example.com"), CreateUser(tenantB, "b@example.com"));
        await database.SaveChangesAsync();
    }

    private static WorkbenchUser CreateUser(Guid tenantId, string email) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Tenant CreateTenant(Guid id, string name) => new()
    {
        Id = id,
        Name = $"Tenant {name}",
        NormalizedName = $"TENANT {name}",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}
