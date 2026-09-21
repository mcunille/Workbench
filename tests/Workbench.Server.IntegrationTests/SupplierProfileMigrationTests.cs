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
        // AND the restricted web command can save plain handles but cannot accept invalid reference text or direct table writes.
        var profiles = content with { SocialProfiles = [new("Discord", "@example"), new("Custom label", "user reference")] };
        var updated = await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, saved.Version, profiles));
        foreach (var invalid in new[] {
            profiles with { SocialProfiles = [new("Discord", "")] },
            profiles with { SocialProfiles = [new("Discord", "line\nbreak")] },
            profiles with { SocialProfiles = [new("Discord", new string('a', 2049))] },
            profiles with { SocialProfiles = [new("Discord", "one"), new("discord", "two")] },
            profiles with { SocialProfiles = Enumerable.Range(0, 21).Select(i => new SupplierSocialProfile($"Label{i}", "handle")).ToArray() } })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, updated.Version, invalid)));
            Assert.Equal(50500, error.Number);
        }
        // AND malformed nested objects and non-array profiles are rejected at the restricted SQL boundary.
        foreach (var raw in new[] { "{}", "[null]", "[{}]", "[{\"label\":\"Discord\",\"handle\":1}]", "[{\"label\":\"Discord\",\"handle\":\"a\",\"extra\":\"b\"}]", "[{\"label\":\"Discord\",\"label\":\"Other\",\"handle\":\"a\"}]" })
        {
            var malformed = System.Text.Json.Nodes.JsonNode.Parse(SupplierCanonical("Update", saved.Id, updated.Version, content))!;
            malformed["supplier"]!["socialProfiles"] = System.Text.Json.Nodes.JsonNode.Parse(raw);
            var invalid = await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(connection, actor, Guid.NewGuid(), malformed.ToJsonString()));
            Assert.Equal(50500, invalid.Number);
        }
        // AND the inclusive entry and text limits fit the command envelope even with escaped Unicode.
        var maximum = content with { SocialProfiles = Enumerable.Range(0, 20).Select(i => new SupplierSocialProfile($"Label{i}" + new string('界', 90), new string('界', 2048))).ToArray() };
        updated = await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, updated.Version, maximum)); await using var direct = new SqlCommand("UPDATE Purchasing.Suppliers SET SocialProfilesJson=NULL", connection);
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => direct.ExecuteNonQueryAsync())).Number);
        // AND another tenant cannot read this supplier or its profiles.
        await using var foreign = await Open(database, web, other);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Purchasing.Suppliers", foreign);
        Assert.Equal(0, await count.ExecuteScalarAsync());
        // WHEN clearing every profile THEN all become absent while the website is retained.
        await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", saved.Id, updated.Version, content));
        await using var cleared = new SqlCommand("SELECT COUNT(*) FROM Purchasing.Suppliers WHERE SocialProfilesJson IS NULL AND Website=N'https://example.test'", connection);
        Assert.Equal(1, await cleared.ExecuteScalarAsync());
    }
    [Fact]
    public async Task SupplierProfilesUseOneMigrationFromTheReleaseBase()
    {
        // GIVEN the released schema WHEN the supplier profile change is applied.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("HardenPurchaseOrderDocumentAuthority");
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        // THEN exactly one migration adds the final JSON field without fixed-platform columns.
        await using var migrations = new SqlCommand("SELECT MigrationId FROM dbo.__EFMigrationsHistory WHERE MigrationId>N'20260918063409_HardenPurchaseOrderDocumentAuthority' ORDER BY MigrationId", connection);
        var applied = new List<string>();
        await using (var reader = await migrations.ExecuteReaderAsync())
            while (await reader.ReadAsync()) applied.Add(reader.GetString(0));
        Assert.Equal("20260921041331_MakeSupplierProfilesCustom", Assert.Single(applied));
        await using var columns = new SqlCommand("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'Purchasing.Suppliers') AND name IN(N'Instagram',N'X',N'GemRockAuctions')", connection);
        Assert.Equal(0, await columns.ExecuteScalarAsync());
        columns.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'Purchasing.Suppliers') AND name=N'SocialProfilesJson' AND is_nullable=1 AND max_length=-1";
        Assert.Equal(1, await columns.ExecuteScalarAsync());
    }
}
