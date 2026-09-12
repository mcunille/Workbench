// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionDocumentDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PurchasingUpgradeRetainsPredecessorDocumentContentAndIdentity()
    {
        // GIVEN the immediate predecessor schema containing a published document and its collection context.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddAcquisitionDocuments");
        var tenant = Guid.NewGuid(); await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        var proof = new Workbench.Server.Tenancy.TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var root = Path.Combine(Path.GetTempPath(), "workbench-purchasing-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var saved = await AcquisitionDocumentFixture.UploadAsync(database.AdminConnectionString, web, proof, tenant, new Workbench.Server.Storage.FileSystemBlobStore(root));
            await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
            await using (var identity = new SqlCommand("""
                INSERT [Identity].[Users](Id,TenantId,State,CreatedAtUtc,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
                VALUES(NEWID(),@tenant,1,SYSUTCDATETIME(),0,0,0,0,0)
                """, admin))
            {
                identity.Parameters.AddWithValue("@tenant", tenant); await identity.ExecuteNonQueryAsync();
            }
            async Task<string> Snapshot()
            {
                await using var read = new SqlCommand("""
                    SELECT
                        (SELECT * FROM Tenancy.Tenants ORDER BY Id FOR JSON PATH) Tenants,
                        (SELECT * FROM [Identity].[Users] ORDER BY Id FOR JSON PATH) Users,
                        (SELECT * FROM Inventory.Items ORDER BY Id FOR JSON PATH) Items,
                        (SELECT * FROM Inventory.Acquisitions ORDER BY Id FOR JSON PATH) Acquisitions,
                        (SELECT * FROM Inventory.AcquisitionItems ORDER BY ItemId FOR JSON PATH) Links,
                        (SELECT * FROM Inventory.AcquisitionDocuments ORDER BY Id FOR JSON PATH) Documents,
                        (SELECT * FROM Inventory.AcquisitionDocumentOperations ORDER BY Id FOR JSON PATH) Operations,
                        (SELECT * FROM Storage.Attachments ORDER BY Id FOR JSON PATH) Attachments,
                        (SELECT * FROM Storage.Revisions ORDER BY Id FOR JSON PATH) Revisions
                    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
                    """, admin);
                return (string)(await read.ExecuteScalarAsync())!;
            }
            var before = await Snapshot();
            // WHEN the purchasing migration is applied THEN all existing values, links and row versions survive unchanged.
            await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
            Assert.Equal(before, await Snapshot());
            await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts)", admin);
            Assert.Equal(0, await count.ExecuteScalarAsync());
            // AND the previously published document remains readable through its original metadata.
            await using var document = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocuments WHERE Id=@id AND RevisionId=@revision", admin);
            document.Parameters.AddWithValue("@id", saved.DocumentId); document.Parameters.AddWithValue("@revision", saved.Revision.Id);
            Assert.Equal(1, await document.ExecuteScalarAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestrictedRuntimeCanReadButCannotMutateDocumentEvidenceDirectly()
    {
        // GIVEN the fully migrated database and its restricted runtime principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        // WHEN inspecting each document table THEN read access exists and direct writes are denied.
        foreach (var table in new[] { "AcquisitionDocuments", "AcquisitionDocumentOperations" })
        {
            await using var read = new SqlCommand($"SELECT COUNT(*) FROM Inventory.{table}", connection);
            Assert.Equal(0, await read.ExecuteScalarAsync());
            foreach (var statement in new[] { $"UPDATE Inventory.{table} SET TenantId=NEWID()", $"DELETE FROM Inventory.{table}", $"INSERT Inventory.{table}(TenantId) VALUES(NEWID())" })
            {
                await using var denied = new SqlCommand(statement, connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
            }
        }
    }
    [Fact]
    public async Task DestructiveDownRetainsSavedDocumentAndReplayEvidence()
    {
        // GIVEN a current database containing a real published acquisition document.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        var proof = new Workbench.Server.Tenancy.TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var root = Path.Combine(Path.GetTempPath(), "workbench-document-down-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var saved = await AcquisitionDocumentFixture.UploadAsync(database.AdminConnectionString, web, proof, tenant, new Workbench.Server.Storage.FileSystemBlobStore(root));
            var migrator = await database.CreateRoleUserAsync("workbench_migrator");
            // WHEN the tenant-filtered migrator requests rollback THEN the guard preserves metadata and command evidence.
            Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => Workbench.Server.Persistence.DatabaseMigrator.MigrateToAsync(migrator, "AddSharedAcquisitions", default))).Number);
            await using var connection = new SqlConnection(database.AdminConnectionString); await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocuments WHERE Id=@id;", connection);
            command.Parameters.AddWithValue("@id", saved.DocumentId); Assert.Equal(1, await command.ExecuteScalarAsync());
            await using var evidence = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocumentOperations WHERE DocumentId=@id AND State=1", connection);
            evidence.Parameters.AddWithValue("@id", saved.DocumentId); Assert.Equal(1, await evidence.ExecuteScalarAsync());
        }
        finally { Directory.Delete(root, true); }
    }
}
