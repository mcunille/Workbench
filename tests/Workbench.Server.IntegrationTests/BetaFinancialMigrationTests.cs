// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Workbench.Server.Persistence;
using Workbench.Server.Persistence.Migrations;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task RetainedBetaBranchUpgradesWithoutRewritingHistoryOrDraftReceipts()
    {
        // GIVEN the retained beta branch, where the parallel PO-05 migrations have never run.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddSupplierBasedDraftPricing");
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var migration in new Migration[] { new ConsolidateBetaDraftCommands(), new RemoveHistoricalDraftReplay() })
        {
            foreach (var operation in migration.UpOperations.Cast<SqlOperation>())
            {
                await using var apply = new SqlCommand(operation.Sql, admin);
                await apply.ExecuteNonQueryAsync();
            }
            var id = migration is ConsolidateBetaDraftCommands
                ? "20260917080000_ConsolidateBetaDraftCommands" : "20260918010000_RemoveHistoricalDraftReplay";
            await using var history = new SqlCommand("INSERT dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES(@id,N'10.0.11')", admin);
            history.Parameters.AddWithValue("@id", id);
            await history.ExecuteNonQueryAsync();
        }
        // AND lifecycle inspection recognizes this explicitly supported retained branch before migration.
        var pending = await Workbench.Server.Administration.DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);
        Assert.True(pending.MigrationHistoryCompatible);
        Assert.False(pending.SchemaCurrent);
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        var webConnectionString = await database.CreateWebUserAsync();
        await using var connection = await Open(database, webConnectionString, tenant);
        var readiness = new DatabaseReadinessCheck(webConnectionString, new TenantContextProof(await database.GetTenantContextProofKeyAsync()));
        // AND the current application must reject an older beta schema despite its own compatible self-report.
        Assert.Equal(HealthStatus.Unhealthy, (await readiness.CheckHealthAsync(new HealthCheckContext())).Status);
        // AND the old beta payload has a saved draft and immutable receipt.
        var oldInput = System.Text.Json.Nodes.JsonNode.Parse(Canonical("Create", null, null, "Retained beta draft"))!;
        oldInput["draft"]!.AsObject().Remove("orderDiscount"); oldInput["draft"]!.AsObject().Remove("charges");
        var canonical = oldInput.ToJsonString();
        var saved = await Save(connection, actor, request, canonical, "Create");
        async Task<string> Snapshot()
        {
            await using var read = new SqlCommand("SELECT (SELECT Id,TenantId,IsDeleted,Title,SupplierName,PoNumber,SupplierId,SupplierContactName,SupplierEmail,SupplierPhone,SupplierWebsite,SupplierPostalAddress,SupplierOrderReference,Platform,Currency,Notes,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId,RowVersion FROM Purchasing.DraftOrders FOR JSON PATH) Drafts,(SELECT * FROM Purchasing.DraftOrderRequestReceipts FOR JSON PATH) Receipts FOR JSON PATH", connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // WHEN pending migrations reconcile the two branches THEN data and successful beta retry bytes survive.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot());
        await using var state = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE State<>'Draft' OR Revision<>0 OR OrderDate IS NOT NULL", connection);
        Assert.Equal(0, await state.ExecuteScalarAsync());
        Assert.True((await Workbench.Server.Administration.DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default)).SchemaCurrent);
        Assert.Equal(HealthStatus.Healthy, (await readiness.CheckHealthAsync(new HealthCheckContext())).Status);
        // AND the SQL compatibility result honors a caller's required schema rather than its own default.
        await using (var incompatible = new SqlCommand("Security.ReadDatabaseReadiness", connection) { CommandType = System.Data.CommandType.StoredProcedure })
        {
            incompatible.Parameters.AddWithValue("@ExpectedMigration", "future-application-schema");
            await using var reader = await incompatible.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
        }
        Assert.True((await Save(connection, actor, request, canonical, "Create")).Replayed);
        // AND only the current financial beta writer remains, with the required readiness marker.
        await using var definition = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SaveDraftOrder'))", admin);
        var sql = (string)(await definition.ExecuteScalarAsync())!;
        Assert.Contains("ContentSchemaVersion=4", sql);
        Assert.Contains("@SavedSupplierId", sql);
        definition.CommandText = "SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N'Purchasing') AND (name LIKE '%DraftOrderV[234]' OR name=N'ReplayDraftOrderReceipt')";
        Assert.Equal(0, Convert.ToInt32(await definition.ExecuteScalarAsync()));
        definition.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'))";
        Assert.Contains("20260921012247_AddSupplierProfiles", (string)(await definition.ExecuteScalarAsync())!);
        // AND a new financial request is accepted and stored using content schema 4.
        await Save(connection, actor, Guid.NewGuid(), Canonical("Create", null, null, "Financial beta draft"), "Create");
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "RemoveHistoricalDraftReplay", default))).Number);
    }
}
