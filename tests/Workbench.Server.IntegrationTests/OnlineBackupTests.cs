// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class OnlineBackupTests
{
    internal static readonly BackupMetadata Metadata = new(Guid.NewGuid(), "https://source.blob.core.windows.net/workbench",
        "/subscriptions/test/resourceGroups/test/providers/Microsoft.Sql/servers/test/databases/Workbench", "Workbench",
        7, "sha256:" + new string('a', 64), "schema", ["certificate-version"]);

    [Fact]
    public async Task CapturesTheListedImmutableVersionAndChecksDestinationBytes()
    {
        // GIVEN a published version whose current blob changes during inventory.
        var source = new Source();
        var archive = new Archive();
        // WHEN the version-copy consumer copies that version without workload control authority.
        var result = await OnlineBackup.CaptureAsync(source, archive, [], Metadata, Guid.NewGuid(), 9, TimeProvider.System, default);
        // THEN the catalog describes the listed version and verified bytes, not the changed current blob.
        Assert.Equal("IntegrityChecked", result.Outcome);
        var entry = Assert.Single(result.Objects);
        Assert.Equal(source.Version, entry.Source);
        Assert.Equal(new byte[] { 1, 2, 3 }, archive.Files[entry.Destination]);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])), entry.Sha256);
        Assert.Single(archive.Files.Keys, key => key.EndsWith("catalog.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingVersionPublishesIncompleteCatalogWithoutClaimingFreshness()
    {
        // GIVEN an enumerated source version that is no longer retained.
        var archive = new Archive();
        // WHEN collection attempts to read the exact version.
        var result = await OnlineBackup.CaptureAsync(new Source { Error = new FileNotFoundException() }, archive, [],
            Metadata, Guid.NewGuid(), 9, TimeProvider.System, default);
        // THEN the loss is explicit and no successful backup is reported.
        Assert.Equal("Incomplete", result.Outcome);
        Assert.Single(result.Gaps);
        Assert.Empty(result.Objects);
        var catalog = JsonSerializer.Deserialize<BackupCatalog>(Assert.Single(archive.Files).Value)!;
        Assert.Equal("Incomplete", catalog.Outcome);
    }

    [Fact]
    public async Task AccessFailureCannotBecomeMissingContentOrCompletedCatalog()
    {
        // GIVEN a provider denying access rather than reporting an absent version.
        var archive = new Archive();
        // WHEN collection reads the source.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OnlineBackup.CaptureAsync(
            new Source { Error = new UnauthorizedAccessException() }, archive, [], Metadata, Guid.NewGuid(), 9, TimeProvider.System, default));
        // THEN there is no completed catalog.
        Assert.Empty(archive.Files);
    }

    [Fact]
    public async Task CorruptDestinationCannotPublishCompletedCatalog()
    {
        // GIVEN a destination that returns different bytes after upload.
        var archive = new Archive { CorruptRead = true };
        // WHEN destination integrity is checked.
        await Assert.ThrowsAsync<InvalidDataException>(() => OnlineBackup.CaptureAsync(new Source(), archive, [],
            Metadata, Guid.NewGuid(), 9, TimeProvider.System, default));
        // THEN corruption is not represented as a successful capture.
        Assert.DoesNotContain(archive.Files.Keys, key => key.EndsWith("catalog.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionMustCoverSqlAndTwoDayCollectionMargin()
    {
        // GIVEN retention shorter than the SQL recovery window plus collection/retry margin.
        var archive = new Archive();
        // WHEN a capture is requested.
        await Assert.ThrowsAsync<ArgumentException>(() => OnlineBackup.CaptureAsync(new Source(), archive, [],
            Metadata, Guid.NewGuid(), 8, TimeProvider.System, default));
        // THEN no misleading shorter-lived backup is written.
        Assert.Empty(archive.Files);
    }

    internal sealed class Source : IBackupVersionSource
    {
        public BackupVersion Version { get; init; } = new("published", "version-1", "etag-1", 3, Guid.NewGuid(), Guid.NewGuid());
        public byte[] Bytes { get; init; } = [1, 2, 3];
        public bool Listed { get; init; } = true;
        public Exception? Error { get; init; }
        public async IAsyncEnumerable<BackupVersion> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Listed) yield return Version;
        }
        public Task<Stream> OpenAsync(BackupVersion version, CancellationToken cancellationToken)
        {
            Assert.Equal(Version, version);
            return Error is null ? Task.FromResult<Stream>(new MemoryStream(Bytes)) : Task.FromException<Stream>(Error);
        }
    }

    internal sealed class Archive : IBackupArchive
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool CorruptRead { get; init; }
        public async Task CreateAsync(string name, Stream content, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            await content.CopyToAsync(output, cancellationToken);
            if (!Files.TryAdd(name, output.ToArray())) throw new IOException("Existing object.");
        }
        public Task<Stream> OpenAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(CorruptRead ? [4, 5, 6] : Files[name]));
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class OnlineBackupEvidenceTests(FinancialEvidenceRecoveryFixture legacy) : IClassFixture<FinancialEvidenceRecoveryFixture>
{
    [Theory]
    [InlineData("absent")]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("tenant")]
    [InlineData("revision")]
    [InlineData("available")]
    public async Task SqlProtectedEvidenceMustMatchAnArchivedVersion(string condition)
    {
        // GIVEN real posted, removed and purged evidence retained by SQL after upgrade.
        await using var payment = await legacy.OpenAsync();
        var context = payment.Bills;
        var connection = await CollectorAsync(context);
        await using var input = await legacy.Source.Store.OpenReadAsync(new(JournalTestContext.TenantId, legacy.Document.Revision), default);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (condition == "digest") bytes[0] ^= 1;
        if (condition == "length") bytes = [.. bytes, 0];
        var source = new OnlineBackupTests.Source
        {
            Listed = condition != "absent",
            Bytes = bytes,
            Version = new("published", "version-1", "etag-1", bytes.Length,
                condition == "tenant" ? Guid.NewGuid() : JournalTestContext.TenantId,
                condition == "revision" ? Guid.NewGuid() : legacy.Document.Revision)
        };
        var archive = new OnlineBackupTests.Archive();
        // WHEN the production SQL-to-capture consumer reads with only manifest execution authority.
        var result = await OnlineBackup.CaptureFromSqlAsync(connection, legacy.Source.Store.Alias, source, archive,
            OnlineBackupTests.Metadata, Guid.NewGuid(), 9, TimeProvider.System, default);
        // THEN only the exact tenant/revision and SQL content identity can satisfy the obligation.
        Assert.Equal(condition == "available" ? "IntegrityChecked" : "Incomplete", result.Outcome);
        if (condition == "available") Assert.Empty(result.Gaps);
        else Assert.Contains($"sql:{JournalTestContext.TenantId:N}/{legacy.Document.Revision:N}", result.Gaps);
        var catalog = JsonSerializer.Deserialize<BackupCatalog>(archive.Files.Single(x => x.Key.EndsWith("catalog.json", StringComparison.Ordinal)).Value)!;
        Assert.Equal(result.Outcome, catalog.Outcome);
    }

    [Fact]
    public async Task CollectorHasOnlyManifestAuthorityAndCannotCaptureWithoutIt()
    {
        // GIVEN a collector with a direct read-only procedure grant and no runtime/maintenance role.
        await using var payment = await legacy.OpenAsync();
        var connectionString = await CollectorAsync(payment.Bills);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in new[] { "SELECT * FROM Accounting.FinancialEvidenceLinks", "UPDATE Storage.Revisions SET State=3", "EXEC Storage.AssertMigrationReady" })
        {
            await using var forbidden = new SqlCommand(sql, connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => forbidden.ExecuteNonQueryAsync())).Number);
        }
        var archive = new OnlineBackupTests.Archive();
        // WHEN required SQL inventory permission disappears, or the provider binding differs.
        await Assert.ThrowsAsync<InvalidDataException>(() => OnlineBackup.CaptureFromSqlAsync(connectionString, "wrong-provider",
            new OnlineBackupTests.Source(), archive, OnlineBackupTests.Metadata, Guid.NewGuid(), 9, TimeProvider.System, default));
        await payment.Bills.AdminAsync("REVOKE EXECUTE ON OBJECT::Storage.ExportManifest FROM backup_collector");
        await Assert.ThrowsAsync<SqlException>(() => OnlineBackup.CaptureFromSqlAsync(connectionString, legacy.Source.Store.Alias,
            new OnlineBackupTests.Source(), archive, OnlineBackupTests.Metadata, Guid.NewGuid(), 9, TimeProvider.System, default));
        // THEN neither case creates objects or a successful catalog.
        Assert.Empty(archive.Files);
    }

    private static async Task<string> CollectorAsync(SupplierBillTestContext context)
    {
        var password = "Backup-" + Guid.NewGuid().ToString("N") + "!";
        await context.AdminAsync($"CREATE USER backup_collector WITH PASSWORD='{password}'; GRANT EXECUTE ON OBJECT::Storage.ExportManifest TO backup_collector;");
        return new SqlConnectionStringBuilder(context.Journal.Application.AdminConnectionString)
        { UserID = "backup_collector", Password = password }.ConnectionString;
    }
}
