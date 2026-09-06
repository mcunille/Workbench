// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Workbench.Server.Authorization;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Operations;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class BlobRecoveryTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PairedBackupRestoresContentAndMigrationPreservesIdentity()
    {
        // GIVEN an offline installation with one retained attachment and dedicated maintenance authority.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        var maintenance = await database.CreateRoleUserAsync("workbench_storage_maintenance");
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var root = Path.Combine(Path.GetTempPath(), "workbench-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
        var snapshot = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
        var installation = Guid.NewGuid();
        var settings = new
        {
            Storage = new { Provider = "FileSystem", Root = live, DurableVolume = true, InstallationId = installation },
            Target = new { Storage = new { Provider = "FileSystem", Root = snapshot, DurableVolume = true, InstallationId = installation } },
        };
        var configPath = Path.Combine(root, "maintenance.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings));
        var config = new ConfigurationBuilder().AddJsonFile(configPath).Build();
        var source = OperationalConfiguration.CreateStore(config)!;
        var target = OperationalConfiguration.CreateStore(config.GetSection("Target"))!;
        var databaseName = new SqlConnectionStringBuilder(database.AdminConnectionString).InitialCatalog;
        var manifestPath = Path.Combine(root, "manifest.json");
        var options = new Dictionary<string, string>
        {
            ["--offline-confirmation"] = "OFFLINE " + databaseName,
            ["--config-file"] = configPath,
            ["--output-file"] = manifestPath,
            ["--manifest-file"] = manifestPath,
        };
        try
        {
            AttachmentRevisionInfo revision;
            await using (var context = BlobPersistenceTests.CreateContext(web, proof, tenant))
            {
                var actor = new RequestActor(Guid.NewGuid(), tenant, Guid.NewGuid(), new HashSet<string> { AttachmentService.ManagePermission });
                using var bytes = new MemoryStream([8, 9, 10]);
                revision = await new AttachmentService(context, source, actor).UploadAsync(Guid.NewGuid(), Guid.NewGuid(), null, bytes, CancellationToken.None);
            }
            var id = new BlobObjectId(tenant, revision.Id);
            // WHEN SQL BACKUP and a verified provider snapshot are captured while writers are stopped.
            var backup = $"/var/opt/mssql/data/{databaseName}.bak";
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand($"BACKUP DATABASE [{databaseName}] TO DISK = @path WITH COPY_ONLY, CHECKSUM, INIT", connection);
                command.Parameters.AddWithValue("@path", backup);
                await command.ExecuteNonQueryAsync();
            }
            await StorageMaintenanceCommand.RunAsync("snapshot", maintenance, databaseName, options, CancellationToken.None);
            // AND both the SQL database and bytes are restored after losing the live object.
            await source.DeleteAsync(id, CancellationToken.None);
            var master = new SqlConnectionStringBuilder(database.AdminConnectionString) { InitialCatalog = "master", Pooling = false };
            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand($"""
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    RESTORE DATABASE [{databaseName}] FROM DISK = @path WITH REPLACE, CHECKSUM;
                    ALTER DATABASE [{databaseName}] SET MULTI_USER;
                    """, connection) { CommandTimeout = 120 };
                command.Parameters.AddWithValue("@path", backup);
                await command.ExecuteNonQueryAsync();
            }
            SqlConnection.ClearAllPools();
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("EXEC [Administration].[SanitizeRestore] @Now=@now, @CorrelationId=N'blob-recovery-drill'", connection);
                command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
                await command.ExecuteNonQueryAsync();
            }
            // THEN verification blocks reopening until every referenced object is restored.
            await Assert.ThrowsAsync<FileNotFoundException>(() => StorageMaintenanceCommand.RunAsync("verify", maintenance, databaseName, options, CancellationToken.None));
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT [IsPending] FROM [Security].[BlobRecoveryState]", connection);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            var entry = new BlobManifestEntry(tenant, revision.Id, source.Alias, revision.Length, revision.Sha256);
            await BlobMaintenance.CopyAsync(target, source, entry, CancellationToken.None);
            await StorageMaintenanceCommand.RunAsync("verify", maintenance, databaseName, options, CancellationToken.None);
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT [IsPending] FROM [Security].[BlobRecoveryState]", connection);
                Assert.False((bool)(await command.ExecuteScalarAsync())!);
            }
            // GIVEN an interrupted revision whose content has not been published.
            var pending = Guid.NewGuid();
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    INSERT INTO [Storage].[Revisions]
                        ([Id], [TenantId], [AttachmentId], [OperationId], [ActorUserId], [ProviderAlias],
                         [Source], [MediaType], [State], [CreatedAtUtc])
                    SELECT @pending, [TenantId], [AttachmentId], NEWID(), [ActorUserId], [ProviderAlias],
                        [Source], [MediaType], 0, SYSUTCDATETIME() FROM [Storage].[Revisions] WHERE [Id] = @id;
                    """, connection);
                command.Parameters.AddWithValue("@pending", pending);
                command.Parameters.AddWithValue("@id", revision.Id);
                await command.ExecuteNonQueryAsync();
            }
            // WHEN migration would leave pending work bound to the retired provider.
            var blocked = await Assert.ThrowsAsync<SqlException>(() =>
                StorageMaintenanceCommand.RunAsync("migrate", maintenance, databaseName, options, CancellationToken.None));
            // THEN cutover is refused and the original available binding remains authoritative.
            Assert.Equal(50023, blocked.Number);
            Assert.Equal(source.Alias, Assert.Single(await StorageMaintenanceCommand.ReadEntriesAsync(maintenance, CancellationToken.None)).ProviderAlias);
            // GIVEN offline operator reconciliation confirms this test operation has no bytes to retain.
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("UPDATE [Storage].[Revisions] SET [State] = 2 WHERE [Id] = @id", connection);
                command.Parameters.AddWithValue("@id", pending);
                await command.ExecuteNonQueryAsync();
            }
            // WHEN migration resumes, THEN only verified available metadata switches provider.
            await StorageMaintenanceCommand.RunAsync("migrate", maintenance, databaseName, options, CancellationToken.None);
            var migrated = Assert.Single(await StorageMaintenanceCommand.ReadEntriesAsync(maintenance, CancellationToken.None));
            Assert.Equal(target.Alias, migrated.ProviderAlias);
            Assert.Equal(revision.Sha256, migrated.Sha256);
            await BlobMaintenance.VerifyAsync(source, entry, CancellationToken.None);
            await BlobMaintenance.VerifyAsync(target, entry, CancellationToken.None);
            // AND the paired manifest records its schema boundary as well as immutable content identity.
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.True(manifest.RootElement.TryGetProperty("SchemaVersion", out var schema));
            Assert.Equal("20260905222755_AddDurableWork", schema.GetString());
            // WHEN the migrated attachment is deleted after its retention deadline.
            await using var contextAfterMigration = BlobPersistenceTests.CreateContext(web, proof, tenant);
            var attachmentAfterMigration = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(contextAfterMigration.Attachments);
            var deleteActor = new RequestActor(Guid.NewGuid(), tenant, Guid.NewGuid(), new HashSet<string> { AttachmentService.ManagePermission });
            await new AttachmentService(contextAfterMigration, target, deleteActor).DeleteAsync(attachmentAfterMigration.Id, revision.Id, CancellationToken.None);
            await using (var connection = new SqlConnection(database.AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    UPDATE [Storage].[Attachments] SET [DeleteAfterUtc] = DATEADD(day, -1, SYSUTCDATETIME());
                    UPDATE [Operations].[WorkItems] SET [AvailableAtUtc] = DATEADD(day, -1, SYSUTCDATETIME());
                    """, connection);
                await command.ExecuteNonQueryAsync();
            }
            var worker = new WorkProcessor(await database.CreateRoleUserAsync("workbench_worker"), proof,
                new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
                new Workbench.Server.Identity.DisabledIdentityMessageDelivery(),
                new Dictionary<string, IBlobStore> { [target.Alias] = target });
            Assert.True(await worker.RunOnceAsync(CancellationToken.None));
            // THEN resolved historical operations do not require the retired provider for cleanup.
            contextAfterMigration.ChangeTracker.Clear();
            Assert.Equal(WorkState.Completed, (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(contextAfterMigration.WorkItems)).State);
            await Assert.ThrowsAsync<FileNotFoundException>(() => target.OpenReadAsync(id, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
