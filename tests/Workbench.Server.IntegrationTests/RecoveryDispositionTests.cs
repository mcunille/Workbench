// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;
using Microsoft.Extensions.Configuration;
using Workbench.Server.Storage;
using Workbench.Server.Operations;
using Workbench.Server.Authorization;
using Workbench.Server.Tenancy;
using System.Text.Json;
using System.Security.Cryptography;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RecoveryDispositionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task AcceptedLossKeepsSqlRecordRemovesOnlyRecoveredOrphanAndRejectsDownload()
    {
        // GIVEN a sanitized isolated SQL restore containing a file absent from recovered storage.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        var maintenance = await database.CreateRoleUserAsync("workbench_storage_maintenance");
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var root = Path.Combine(Path.GetTempPath(), "workbench-file-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var originalPath = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var targetPath = Directory.CreateDirectory(Path.Combine(root, "recovered")).FullName;
            var installation = Guid.NewGuid();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recovery:Source:Storage:Provider"] = "FileSystem",
                ["Recovery:Source:Storage:Root"] = originalPath,
                ["Recovery:Source:Storage:InstallationId"] = installation.ToString(),
                ["Storage:Provider"] = "FileSystem",
                ["Storage:Root"] = targetPath,
                ["Storage:DurableVolume"] = "true",
                ["Storage:InstallationId"] = installation.ToString(),
            }).Build();
            var original = OperationalConfiguration.CreateStore(configuration.GetSection("Recovery:Source"))!;
            var target = OperationalConfiguration.CreateStore(configuration)!;
            var attachment = Guid.NewGuid();
            var actor = new RequestActor(Guid.NewGuid(), tenant, Guid.NewGuid(), new HashSet<string>
                { AttachmentService.ManagePermission, AttachmentService.ReadPermission });
            AttachmentRevisionInfo revision;
            await using (var context = BlobPersistenceTests.CreateContext(web, proof, tenant))
                revision = await new AttachmentService(context, original, actor).UploadAsync(attachment, Guid.NewGuid(), null, new MemoryStream([1, 2, 3]), default);
            await using var setup = new SqlConnection(database.AdminConnectionString);
            await setup.OpenAsync();
            await using (var mark = new SqlCommand("""
                UPDATE [Security].[DatabaseSecurityState] SET RestoreGeneration=1,RestoreSanitizedGeneration=1;
                UPDATE [Security].[BlobRecoveryState] SET IsPending=1;
                """, setup)) await mark.ExecuteNonQueryAsync();
            var orphan = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
            await target.StageAsync(orphan, new MemoryStream([8]), 1, default);
            await target.PublishAsync(orphan, default);
            var path = Path.Combine(root, "report.json");
            var arguments = new Dictionary<string, string> { ["--report-file"] = path };
            await FileRecoveryCommand.RunAsync("recovery-plan", maintenance, arguments, configuration, target, installation, default);
            var reportBytes = await File.ReadAllBytesAsync(path);
            var report = JsonSerializer.Deserialize<FileRecoveryReport>(reportBytes)!;
            Assert.Equal(revision.Id, Assert.Single(report.Missing).RevisionId);
            // WHEN the exact report is accepted and applied manually.
            arguments["--accept-report-sha256"] = Convert.ToHexString(SHA256.HashData(reportBytes));
            // AND a concurrent SQL change invalidates acceptance before orphan deletion.
            await using (var change = new SqlCommand("UPDATE [Storage].[Revisions] SET [State]=[State]", setup))
                await change.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => FileRecoveryCommand.RunAsync("recovery-apply", maintenance, arguments, configuration, target, installation, default));
            await using (var retainedOrphan = await target.OpenReadAsync(orphan, default)) Assert.Equal(8, retainedOrphan.ReadByte());
            arguments["--report-file"] = Path.Combine(root, "fresh-report.json");
            await FileRecoveryCommand.RunAsync("recovery-plan", maintenance, arguments, configuration, target, installation, default);
            arguments["--accept-report-sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(arguments["--report-file"])));
            await FileRecoveryCommand.RunAsync("recovery-apply", maintenance, arguments, configuration, target, installation, default);
            // THEN the source bytes survive, the recovered orphan is absent, and SQL retains an explicit disposition.
            await using var sourceBytes = await original.OpenReadAsync(new(tenant, revision.Id), default);
            Assert.Equal(1, sourceBytes.ReadByte());
            await Assert.ThrowsAsync<FileNotFoundException>(() => target.OpenReadAsync(orphan, default));
            await using var status = new SqlCommand("SELECT COUNT(*) FROM [Storage].[RecoveryFiles] WHERE Reason='Missing'", setup);
            Assert.Equal(1, (int)(await status.ExecuteScalarAsync())!);
            await using var recoveredContext = BlobPersistenceTests.CreateContext(web, proof, tenant);
            await Assert.ThrowsAsync<RecoveredFileUnavailableException>(() => new AttachmentService(recoveredContext, target, actor).DownloadAsync(attachment, default));
            // AND an acknowledged retry does not reopen guards, delete more files, or add another report.
            await FileRecoveryCommand.RunAsync("recovery-apply", maintenance, arguments, configuration, target, installation, default);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LiveDatabaseCannotIssueRecoveryInventory()
    {
        // GIVEN a live database and the narrow maintenance principal.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var maintenance = await database.CreateRoleUserAsync("workbench_storage_maintenance");
        await using var connection = new SqlConnection(maintenance);
        await connection.OpenAsync();
        // WHEN a recovery report is requested without a sanitized restore generation.
        await using var command = new SqlCommand("EXEC [Storage].[ReadRecoveryInventory]", connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        // THEN the explicit recovery guard rejects it, not a missing procedure/permission error.
        Assert.Equal(50043, error.Number);
    }

    [Fact]
    public async Task OrdinaryWebCannotAcceptMissingFiles()
    {
        // GIVEN the normal application principal, which must not grant itself recovery authority.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var web = await database.CreateWebUserAsync();
        await using var connection = new SqlConnection(web);
        await connection.OpenAsync();
        // WHEN it attempts to modify accepted recovery dispositions.
        await using var command = new SqlCommand("DELETE FROM [Storage].[RecoveryFiles]", connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        // THEN SQL denies the write independently of the HTTP surface.
        Assert.Equal(229, error.Number);
    }
}
