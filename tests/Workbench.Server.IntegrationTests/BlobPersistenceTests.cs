// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Workbench.Server.Storage;
using Workbench.Server.Authorization;
using Xunit;
using Workbench.Server.Operations;
using Workbench.Server.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class BlobPersistenceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ReplacementPreservesPriorContentAndRejectsStaleCommands()
    {
        // GIVEN an authorized tenant and an isolated blob provider.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var connection = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var context = CreateContext(connection, proof, tenant);
        var root = Path.Combine(Path.GetTempPath(), "workbench-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileSystemBlobStore(root);
            var actor = new RequestActor(Guid.NewGuid(), tenant, Guid.NewGuid(),
                new HashSet<string> { AttachmentService.ManagePermission, AttachmentService.ReadPermission });
            var service = new AttachmentService(context, store, actor);
            var attachment = Guid.NewGuid();
            using var original = new MemoryStream([1, 2, 3]);
            var first = await service.UploadAsync(attachment, Guid.NewGuid(), null, original, CancellationToken.None);
            // WHEN new bytes replace the current revision.
            using var replacement = new MemoryStream([4, 5]);
            var second = await service.UploadAsync(attachment, Guid.NewGuid(), first.Id, replacement, CancellationToken.None);
            // THEN the earlier immutable object is retained and only the new revision is current.
            Assert.NotEqual(first.Id, second.Id);
            await using var oldBytes = await store.OpenReadAsync(new BlobObjectId(tenant, first.Id), CancellationToken.None);
            Assert.Equal(1, oldBytes.ReadByte());
            await using var current = await service.DownloadAsync(attachment, CancellationToken.None);
            Assert.Equal(4, current.ReadByte());
            using var stale = new MemoryStream([6]);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                service.UploadAsync(attachment, Guid.NewGuid(), first.Id, stale, CancellationToken.None));
            // AND SQL rejects attempts to alter a completed revision digest.
            await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [Storage].[Revisions] SET [Sha256] = {new string('0', 64)} WHERE [Id] = {first.Id}
                """));
            // WHEN the current attachment is deleted, THEN it is immediately hidden
            // and physical cleanup is durably scheduled after the retention grace.
            await service.DeleteAsync(attachment, second.Id, CancellationToken.None);
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.DownloadAsync(attachment, CancellationToken.None));
            var cleanup = await context.WorkItems.SingleAsync();
            Assert.Equal(attachment, cleanup.AttachmentId);
            Assert.True(cleanup.AvailableAtUtc > DateTimeOffset.UtcNow.AddDays(6));
            // AND an explicit maintenance export retains both immutable revisions.
            await using var maintenanceConnection = new SqlConnection(database.AdminConnectionString);
            await maintenanceConnection.OpenAsync();
            await using var export = new SqlCommand("[Storage].[ExportManifest]", maintenanceConnection)
            { CommandType = System.Data.CommandType.StoredProcedure };
            await using var exported = await export.ExecuteReaderAsync();
            var count = 0;
            while (await exported.ReadAsync()) { count++; }
            Assert.Equal(2, count);
            await exported.DisposeAsync();
            await oldBytes.DisposeAsync();
            await current.DisposeAsync();
            // WHEN the retention grace expires and a dedicated worker processes cleanup.
            await using var expire = new SqlCommand("""
                UPDATE [Storage].[Attachments] SET [DeleteAfterUtc] = DATEADD(day, -1, SYSUTCDATETIME());
                UPDATE [Operations].[WorkItems] SET [AvailableAtUtc] = DATEADD(day, -1, SYSUTCDATETIME());
                """, maintenanceConnection);
            await expire.ExecuteNonQueryAsync();
            var workerConnection = await database.CreateRoleUserAsync("workbench_worker");
            var worker = new WorkProcessor(workerConnection, proof, new EphemeralDataProtectionProvider(),
                new DisabledIdentityMessageDelivery(), new Dictionary<string, IBlobStore> { [store.Alias] = store });
            Assert.True(await worker.RunOnceAsync(CancellationToken.None));
            // THEN bytes are absent and immutable metadata records their completed physical deletion.
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(new BlobObjectId(tenant, first.Id), CancellationToken.None));
            context.ChangeTracker.Clear();
            Assert.All(await context.AttachmentRevisions.ToListAsync(), revision => Assert.Equal(RevisionState.Purged, revision.State));
            Assert.Equal(WorkState.Completed, (await context.WorkItems.SingleAsync()).State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BlobMetadataIsTenantIsolatedEvenWithoutOrmFilters()
    {
        // GIVEN two tenants and a migrated database using the ordinary web principal.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenantA, tenantB);
        var connection = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var contextA = CreateContext(connection, proof, tenantA);
        var attachment = Guid.NewGuid();

        // WHEN tenant A creates an attachment through raw SQL.
        await contextA.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Storage].[Attachments] ([Id], [TenantId], [CreatedAtUtc])
            VALUES ({attachment}, {tenantA}, {DateTimeOffset.UtcNow})
            """);

        // THEN another tenant cannot read it or insert metadata for tenant A.
        await using var contextB = CreateContext(connection, proof, tenantB);
        Assert.Empty(await contextB.Database.SqlQuery<Guid>($"""
            SELECT [Id] AS [Value] FROM [Storage].[Attachments]
            """).ToListAsync());
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            contextB.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Storage].[Attachments] ([Id], [TenantId], [CreatedAtUtc])
                VALUES ({Guid.NewGuid()}, {tenantA}, {DateTimeOffset.UtcNow})
                """));
        Assert.Equal(33504, error.Number);
    }

    internal static WorkbenchDbContext CreateContext(string connection, TenantContextProof proof, Guid tenantId)
    {
        var tenant = new TenantContext(tenantId);
        return new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(connection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant, proof),
                new TenantSaveChangesInterceptor(tenant)).Options, tenant);
    }
}
