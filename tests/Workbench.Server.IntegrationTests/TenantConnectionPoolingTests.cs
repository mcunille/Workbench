// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class TenantConnectionPoolingTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private readonly Guid _tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private SqlTestDatabase _database = null!;
    private string _pooledWebConnectionString = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
        var webConnectionString = await _database.CreateWebUserAsync();
        _pooledWebConnectionString = new SqlConnectionStringBuilder(webConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 1,
        }.ConnectionString;
        await _database.SeedTenantAuditRowsAsync(_tenantA, _tenantB);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task ReusedPhysicalConnectionNeverLeaksPriorTenant()
    {
        Assert.Equal(_tenantA, await ReadOnlyVisibleTenantAsync(new TenantContext(_tenantA)));
        Assert.Equal(_tenantB, await ReadOnlyVisibleTenantAsync(new TenantContext(_tenantB)));
        Assert.Null(await ReadOnlyVisibleTenantAsync(TenantContext.None));
        Assert.Equal(_tenantA, await ReadOnlyVisibleTenantAsync(new TenantContext(_tenantA)));
    }

    [Fact]
    public async Task TenantContextCannotBeChangedWhileConnectionIsOpen()
    {
        await using var database = CreateContext(new TenantContext(_tenantA));
        await database.Database.OpenConnectionAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sys.sp_set_session_context @key=N'TenantId', @value={_tenantB}"));

        Assert.Equal(15664, exception.Number);
    }

    private async Task<Guid?> ReadOnlyVisibleTenantAsync(TenantContext tenantContext)
    {
        await using var database = CreateContext(tenantContext);
        return await database.TenantSecurityAuditEvents
            .Select(row => (Guid?)row.TenantId)
            .SingleOrDefaultAsync();
    }

    private WorkbenchDbContext CreateContext(TenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(_pooledWebConnectionString)
            .AddInterceptors(
                new TenantConnectionInterceptor(tenantContext),
                new TenantSaveChangesInterceptor(tenantContext))
            .Options;

        return new WorkbenchDbContext(options, tenantContext);
    }
}
