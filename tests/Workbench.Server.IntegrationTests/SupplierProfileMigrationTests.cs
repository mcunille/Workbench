// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task SupplierProfileUpgradePreservesLegacyReceiptsAndRestrictedAuthority()
    {
        // GIVEN the PR base schema with a saved supplier and immutable retry receipt.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("HardenPurchaseOrderDocumentAuthority");
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, other);
        await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync();
        await using var connection = await Open(database, web, tenant);
        var content = new SupplierContent("Retained supplier", null, null, null, "https://example.test", null);
        var request = Guid.NewGuid();
        var canonical = SupplierCanonical("Create", null, null, content);
        var saved = await SaveSupplier(connection, actor, request, canonical);
        async Task<string> Snapshot()
        {
            await using var read = new SqlCommand("SELECT (SELECT * FROM Purchasing.Suppliers FOR JSON PATH) Suppliers,(SELECT * FROM Purchasing.SupplierRequestReceipts FOR JSON PATH) Receipts FOR JSON PATH", connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // WHEN upgrading THEN retained values, versions, and receipt bytes remain identical; the old request still replays.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot());
        Assert.True((await SaveSupplier(connection, actor, request, canonical)).Replayed);
        // AND the restricted web command can save safe profiles but cannot accept unsafe links or direct table writes.
        var profiles = content with { Instagram = "https://instagram.com/example", X = "http://x.com/example", GemRockAuctions = "https://www.gemrockauctions.com/stores/example" };
        var updated = await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, saved.Version, profiles));
        foreach (var bad in new[] { "javascript:alert(1)", "https://user:password@example.test", "//example.test", "https://example.test/white space", "https://example.test/" + new string('a', 2048) })
        {
            foreach (var invalid in new[] { profiles with { Instagram = bad }, profiles with { X = bad }, profiles with { GemRockAuctions = bad } })
            {
                var error = await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, updated.Version, invalid)));
                Assert.Equal(50500, error.Number);
            }
        }
        await using var direct = new SqlCommand("UPDATE Purchasing.Suppliers SET Instagram=N'https://instagram.com/direct'", connection);
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => direct.ExecuteNonQueryAsync())).Number);
        // AND another tenant cannot read this supplier or its profiles.
        await using var foreign = await Open(database, web, other);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Purchasing.Suppliers", foreign);
        Assert.Equal(0, await count.ExecuteScalarAsync());
        // WHEN clearing every profile THEN all become absent while the website is retained.
        await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, updated.Version, content));
        await using var cleared = new SqlCommand("SELECT COUNT(*) FROM Purchasing.Suppliers WHERE Instagram IS NULL AND X IS NULL AND GemRockAuctions IS NULL AND Website=N'https://example.test'", connection);
        Assert.Equal(1, await cleared.ExecuteScalarAsync());
    }
}
