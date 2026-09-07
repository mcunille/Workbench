// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class OnlineBackupTests
{
    private static readonly BackupMetadata Metadata = new(Guid.NewGuid(), "https://source.blob.core.windows.net/workbench",
        "/subscriptions/test/resourceGroups/test/providers/Microsoft.Sql/servers/test/databases/Workbench", "Workbench",
        7, "sha256:" + new string('a', 64), "schema", ["certificate-version"]);

    [Fact]
    public async Task CapturesTheListedImmutableVersionAndChecksDestinationBytes()
    {
        // GIVEN a published version whose current blob changes during inventory.
        var source = new Source();
        var archive = new Archive();
        // WHEN the online collector copies that version without SQL or workload control authority.
        var result = await OnlineBackup.CaptureAsync(source, archive, Metadata, Guid.NewGuid(), 9, TimeProvider.System, default);
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
        var result = await OnlineBackup.CaptureAsync(new Source { Error = new FileNotFoundException() }, archive,
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
            new Source { Error = new UnauthorizedAccessException() }, archive, Metadata, Guid.NewGuid(), 9, TimeProvider.System, default));
        // THEN there is no completed catalog.
        Assert.Empty(archive.Files);
    }

    [Fact]
    public async Task CorruptDestinationCannotPublishCompletedCatalog()
    {
        // GIVEN a destination that returns different bytes after upload.
        var archive = new Archive { CorruptRead = true };
        // WHEN destination integrity is checked.
        await Assert.ThrowsAsync<InvalidDataException>(() => OnlineBackup.CaptureAsync(new Source(), archive,
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
        await Assert.ThrowsAsync<ArgumentException>(() => OnlineBackup.CaptureAsync(new Source(), archive,
            Metadata, Guid.NewGuid(), 8, TimeProvider.System, default));
        // THEN no misleading shorter-lived backup is written.
        Assert.Empty(archive.Files);
    }

    private sealed class Source : IBackupVersionSource
    {
        public BackupVersion Version { get; } = new("published", "version-1", "etag-1", 3, Guid.NewGuid(), Guid.NewGuid());
        public Exception? Error { get; init; }
        public async IAsyncEnumerable<BackupVersion> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return Version;
        }
        public Task<Stream> OpenAsync(BackupVersion version, CancellationToken cancellationToken)
        {
            Assert.Equal(Version, version);
            return Error is null ? Task.FromResult<Stream>(new MemoryStream([1, 2, 3])) : Task.FromException<Stream>(Error);
        }
    }

    private sealed class Archive : IBackupArchive
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
