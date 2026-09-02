// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class TenantIsolationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private readonly Guid _tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private SqlTestDatabase _database = null!;
    private string _webConnectionString = null!;
    private TenantContextProof _tenantContextProof = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(_database.AdminConnectionString, CancellationToken.None);
        _webConnectionString = await _database.CreateWebUserAsync();
        _tenantContextProof = new TenantContextProof(await _database.GetTenantContextProofKeyAsync());
        await _database.SeedTenantAuditRowsAsync(_tenantA, _tenantB);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task IgnoreQueryFiltersCannotReadAnotherTenant()
    {
        await using var database = CreateContext(new TenantContext(_tenantA));

        var rows = await database.TenantSecurityAuditEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(_tenantA, row.TenantId);
    }

    [Fact]
    public async Task MissingTenantContextReadsNothingAndCannotInsert()
    {
        await using var database = CreateContext(TenantContext.None);

        Assert.Empty(await database.TenantSecurityAuditEvents.ToListAsync());

        database.TenantSecurityAuditEvents.Add(new TenantSecurityAuditEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantA,
            Action = "test.denied",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task RawSqlCannotInsertForAnotherTenant()
    {
        await using var database = CreateContext(new TenantContext(_tenantA));

        var exception = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Security].[TenantSecurityAuditEvents]
                ([Id], [TenantId], [Action], [OccurredAtUtc])
            VALUES
                ({Guid.CreateVersion7()}, {_tenantB}, {"test.cross-tenant"}, {DateTimeOffset.UtcNow})
            """));

        Assert.Equal(33504, exception.Number);
    }

    [Fact]
    public async Task TenantOwnershipCannotBeChanged()
    {
        await using var database = CreateContext(new TenantContext(_tenantA));
        var row = await database.TenantSecurityAuditEvents.SingleAsync();
        row.TenantId = _tenantB;

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task WebCredentialCannotSelectATenantWithoutApplicationProof()
    {
        await using var connection = new SqlConnection(_webConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=1",
            connection))
        {
            context.Parameters.AddWithValue("@tenantId", _tenantA);
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [Security].[TenantSecurityAuditEvents]",
            connection);

        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    private WorkbenchDbContext CreateContext(TenantContext tenantContext)
    {
        var interceptor = new TenantConnectionInterceptor(tenantContext, _tenantContextProof);
        var saveInterceptor = new TenantSaveChangesInterceptor(tenantContext);
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(_webConnectionString)
            .AddInterceptors(interceptor, saveInterceptor)
            .Options;

        return new WorkbenchDbContext(options, tenantContext);
    }
}
