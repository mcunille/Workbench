// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InventoryDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedPrincipalCannotReadOrInsertForeignItemsOrMutateSavedIdentity()
    {
        // GIVEN two tenants with records and a real runtime connection proved for tenant A.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenantA, tenantB);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await InsertAsync(admin, tenantA, "A");
        await InsertAsync(admin, tenantB, "B");
        await using var web = new SqlConnection(await database.CreateWebUserAsync());
        await web.OpenAsync();
        await new TenantContextProof(await database.GetTenantContextProofKeyAsync()).ApplyAsync(web, tenantA, CancellationToken.None);
        // WHEN raw SQL bypasses application and EF filters.
        await using var read = new SqlCommand("SELECT [Name] FROM [Inventory].[Items]", web);
        await using (var reader = await read.ExecuteReaderAsync())
        {
            // THEN SQL RLS returns only the active tenant's record.
            Assert.True(await reader.ReadAsync());
            Assert.Equal("A", reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }
        var denied = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(web, tenantB, "Foreign"));
        Assert.Equal(33504, denied.Number);
        // AND H1 cannot mutate or delete even an owned record.
        foreach (var sql in new[] { "UPDATE [Inventory].[Items] SET [TrackingKind]='Lot'", "DELETE FROM [Inventory].[Items]" })
        {
            await using var command = new SqlCommand(sql, web);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        await using var count = new SqlCommand("SELECT COUNT(*) FROM [Inventory].[Items]", admin);
        Assert.Equal(2, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("", "Individual", false)]
    [InlineData(" \t\r\n\u2003\u00a0", "Individual", false)]
    [InlineData("Valid", "Lot", false)]
    [InlineData("Valid", "Individual", true)]
    public async Task DatabaseEnforcesPhysicalHoldingAndNonblankName(string name, string kind, bool valid)
    {
        // GIVEN the current schema and a seeded tenant.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenantA = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenantA, Guid.NewGuid());
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        // WHEN direct SQL supplies supported or unsupported item semantics.
        var error = await Record.ExceptionAsync(() => InsertAsync(admin, tenantA, name, kind));
        // THEN database constraints reject invalid holdings independently of HTTP.
        if (valid)
            Assert.Null(error);
        else
            Assert.Equal(547, Assert.IsType<SqlException>(error).Number);
    }

    private static async Task InsertAsync(SqlConnection connection, Guid tenant, string name, string kind = "Individual")
    {
        await using var insert = new SqlCommand("""
            INSERT [Inventory].[Items] ([Id], [TenantId], [TrackingKind], [Name], [CreatedAtUtc], [CreationRequestId])
            VALUES (NEWID(), @tenant, @kind, @name, SYSUTCDATETIME(), NEWID());
            """, connection);
        insert.Parameters.AddWithValue("@tenant", tenant);
        insert.Parameters.AddWithValue("@kind", kind);
        insert.Parameters.AddWithValue("@name", name);
        await insert.ExecuteNonQueryAsync();
    }
}
