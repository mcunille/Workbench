// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceMigrationTests(SqlServerFixture sqlServer)
{
    private const string Baseline = "20260928071548_AddSupplierOpenItems";

    [Fact]
    public async Task SharedReferenceUpgradePreservesMergedBaseDataAndMovesReadiness()
    {
        // GIVEN the merged predecessor with existing tenant and item bytes.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, Baseline, default);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT Tenancy.Tenants(Id,Name,NormalizedName,IsEnabled,CreatedAtUtc)
            VALUES('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',N'Existing tenant',N'EXISTING TENANT',1,'2026-09-29T00:00:00+00:00');
            INSERT Inventory.Items(Id,TenantId,TrackingKind,Name,Notes,StorageLocation,CreationRequestId,CreatedAtUtc)
            VALUES('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                N'Individual',N'Owner entered name',N'Original notes',N'Tray A',
                'cccccccc-cccc-cccc-cccc-cccccccccccc','2026-09-29T00:00:00+00:00');
            """, connection);
        await seed.ExecuteNonQueryAsync();
        var before = await ItemStateAsync(connection);
        Assert.True(await CompatibleAsync(connection, Baseline));
        Assert.False(await CompatibleAsync(connection, CurrentSchema.MigrationId));
        // WHEN upgrading THEN tenant content and rowversion survive without seeding a catalog.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await ItemStateAsync(connection));
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Gemology.Entries", connection);
        Assert.Equal(0, await count.ExecuteScalarAsync());
        Assert.True(await CompatibleAsync(connection, CurrentSchema.MigrationId));
        Assert.False(await CompatibleAsync(connection, Baseline));
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
        // AND the upgraded actual web principal can read shared data without mutation authority.
        await using var web = new SqlConnection(await database.CreateWebUserAsync());
        await web.OpenAsync();
        await using var read = new SqlCommand("SELECT COUNT(*) FROM Gemology.Entries", web);
        Assert.Equal(0, await read.ExecuteScalarAsync());
        await using var write = new SqlCommand("DELETE Gemology.Entries", web);
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => write.ExecuteNonQueryAsync())).Number);
    }

    [Fact]
    public async Task SharedReferenceRollbackIsGuarded()
    {
        // GIVEN current storage with a retained identity and source assertions.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, GemReferenceSamples.Mineral());
        // WHEN reverting THEN the guard rejects rollback without removing data or migration history.
        var failure = await Assert.ThrowsAsync<SqlException>(() =>
            DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, Baseline, default));
        Assert.Equal(50020, failure.Number);
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Gemology.SourceAssertions WHERE EntryId='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'", connection);
        Assert.Equal(4, await count.ExecuteScalarAsync());
        Assert.True(await CompatibleAsync(connection, CurrentSchema.MigrationId));
    }

    private static async Task<string> ItemStateAsync(SqlConnection connection)
    {
        await using var state = new SqlCommand("""
            SELECT tenant.Name AS TenantName,item.Name AS ItemName,item.Notes,item.StorageLocation,item.CreationRequestId,
                item.CreatedAtUtc,item.RowVersion FROM Inventory.Items item JOIN Tenancy.Tenants tenant
                ON tenant.Id=item.TenantId FOR JSON PATH
            """, connection);
        return (string)(await state.ExecuteScalarAsync())!;
    }

    private static async Task<bool> CompatibleAsync(SqlConnection connection, string migration)
    {
        await using var command = new SqlCommand("Security.ReadDatabaseReadiness", connection)
        { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ExpectedMigration", migration);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetBoolean(reader.GetOrdinal("CompatibleMigration"));
    }
}
