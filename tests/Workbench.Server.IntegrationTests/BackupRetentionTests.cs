// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BackupRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Installation = Guid.NewGuid();

    [Fact]
    public async Task DeletesExpiredRunObjectsBeforeCatalogAndRetriesSafely()
    {
        // GIVEN an expired run whose objects and catalog are older than the 45-day safety threshold.
        var archive = new Archive();
        var (obj, catalog) = archive.AddRun(46, -8);
        // WHEN expiration runs twice, THEN only the first run deletes, with its catalog last.
        Assert.Equal(2, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Equal(new[] { obj.Name, catalog.Name }, archive.Deleted);
        Assert.Equal(0, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
    }

    [Theory]
    [InlineData(44, -1)]
    [InlineData(46, 1)]
    public async Task PreservesYoungObjectsAndRetainedCatalogs(int age, int expiry)
    {
        // GIVEN either a recent run or a catalog still needed for recovery.
        var archive = new Archive();
        archive.AddRun(age, expiry);
        // WHEN expiration runs, THEN neither content nor catalog is deleted.
        Assert.Equal(0, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Empty(archive.Deleted);
    }

    [Fact]
    public async Task RejectsCrossRunReferencesBeforeAnyDeletion()
    {
        // GIVEN an expired candidate and a retained catalog referring to its bytes.
        var archive = new Archive();
        var old = archive.AddRun(46, -8);
        var retained = archive.AddRun(1, 8);
        archive.Catalogs[retained.Item2.Name] = archive.Catalogs[retained.Item2.Name] with
        { Objects = [archive.Catalogs[old.Item2.Name].Objects[0]] };
        // WHEN validating catalog ownership, THEN malformed cross-run references fail closed globally.
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Empty(archive.Deleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedOrProtectedObjectsStopCleanupAndPreserveCatalog(bool concurrency)
    {
        // GIVEN an expired run where Azure rejects deletion (ETag change or remaining WORM/legal hold).
        var archive = new Archive { DeleteError = concurrency ? new IOException("ETag changed") : new UnauthorizedAccessException() };
        var run = archive.AddRun(46, -8);
        // WHEN deletion fails, THEN the failure propagates and the catalog remains for retry.
        if (concurrency) await Assert.ThrowsAsync<IOException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Contains(run.Item2, archive.Items);
        Assert.Empty(archive.Deleted);
    }

    [Fact]
    public async Task OldIncompleteRunWithoutCatalogCanExpireButNewOrUnknownObjectsCannot()
    {
        // GIVEN abandoned upload bytes, a recent object in another run and an unrecognized name.
        var archive = new Archive();
        var abandoned = archive.AddRun(46, -8);
        archive.Items.Remove(abandoned.Item2);
        archive.Catalogs.Remove(abandoned.Item2.Name);
        archive.AddRun(1, 8);
        // WHEN collecting expiration, THEN abandoned old bytes are removed without touching the recent run.
        Assert.Equal(1, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
        archive.Items.Add(new("outside-installation/object", "tag", Now.AddDays(-50), Now.AddDays(-50)));
        // AND an unexpected namespace fails closed rather than authorizing arbitrary deletion.
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Single(archive.Deleted);
    }

    [Fact]
    public async Task RecentModificationAndMissingRetainedObjectsBlockUnsafeExpiry()
    {
        // GIVEN old creation timestamps but a recently changed object.
        var archive = new Archive();
        var run = archive.AddRun(46, -8);
        archive.Items[0] = run.Item1 with { ModifiedAtUtc = Now };
        // WHEN expiring, THEN the whole run remains intact.
        Assert.Equal(0, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
        var current = archive.AddRun(1, 8);
        archive.Items.Remove(current.Item1);
        // AND a missing retained reference is an integrity failure, not permission to continue deleting.
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Empty(archive.Deleted);
    }

    [Theory]
    [InlineData(true, false, "Locked", 37)]
    [InlineData(false, true, "Locked", 37)]
    [InlineData(false, false, "Unlocked", 37)]
    [InlineData(false, false, "Locked", 36)]
    public void ProtectionDriftRejectsExpiration(bool versioning, bool softDelete, string state, int days)
    {
        // GIVEN hidden-version retention or insufficient WORM protection.
        using var account = System.Text.Json.JsonDocument.Parse("""{"properties":{"publicNetworkAccess":"Disabled"},"sku":{"name":"Standard_GRS"}}""");
        using var service = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { properties = new { isVersioningEnabled = versioning, deleteRetentionPolicy = new { enabled = softDelete } } }));
        using var policy = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { properties = new { state, immutabilityPeriodSinceCreationInDays = days } }));
        // WHEN checking archive eligibility, THEN cleanup cannot silently use different protection/deletion semantics.
        Assert.Throws<InvalidOperationException>(() => BackupRetentionCommand.ValidateProtection(account.RootElement, service.RootElement, policy.RootElement));
    }

    [Fact]
    public async Task InterruptedRunKeepsCatalogUntilRemainingDeletesSucceed()
    {
        // GIVEN a failure after deleting content but before removing its expired catalog.
        var archive = new Archive { FailOnDeleteNumber = 2 };
        var run = archive.AddRun(46, -8);
        await Assert.ThrowsAsync<IOException>(() => BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Equal(new[] { run.Item1.Name }, archive.Deleted);
        // WHEN retried, THEN the remaining expired catalog is removed without requiring already-deleted bytes.
        Assert.Equal(1, await BackupRetention.ExpireAsync(archive, Installation, Now, default));
        Assert.Equal(new[] { run.Item1.Name, run.Item2.Name }, archive.Deleted);
    }

    [Fact]
    public void PrivateImmutableArchiveWithDirectDeletionSemanticsIsEligible()
    {
        // GIVEN private geo-redundant storage with sufficient locked protection and no hidden versions.
        using var account = System.Text.Json.JsonDocument.Parse("""{"properties":{"publicNetworkAccess":"Disabled"},"sku":{"name":"Standard_GRS"}}""");
        using var service = System.Text.Json.JsonDocument.Parse("""{"properties":{"isVersioningEnabled":false,"deleteRetentionPolicy":{"enabled":false}}}""");
        using var policy = System.Text.Json.JsonDocument.Parse("""{"properties":{"state":"Locked","immutabilityPeriodSinceCreationInDays":37}}""");
        // WHEN validating, THEN normal protection is accepted without changing any Azure policy.
        BackupRetentionCommand.ValidateProtection(account.RootElement, service.RootElement, policy.RootElement);
    }

    private sealed class Archive : IBackupRetentionArchive
    {
        public List<RetentionObject> Items { get; } = [];
        public Dictionary<string, BackupCatalog> Catalogs { get; } = [];
        public List<string> Deleted { get; } = [];
        public Exception? DeleteError { get; init; }
        public int FailOnDeleteNumber { get; init; } = int.MaxValue;
        private int _deleteAttempts;
        public (RetentionObject, RetentionObject) AddRun(int age, int expiry)
        {
            var run = Guid.NewGuid();
            var prefix = $"{Installation:N}/{run:N}/";
            var obj = new RetentionObject(prefix + "objects/" + new string('A', 64), "object-tag", Now.AddDays(-age), Now.AddDays(-age));
            var catalog = new RetentionObject(prefix + "catalog.json", "catalog-tag", Now.AddDays(-age), Now.AddDays(-age));
            Items.AddRange([obj, catalog]);
            Catalogs[catalog.Name] = new(1, run, new(Installation, "source", "sql", "Workbench", 7, "digest", "schema", ["key"]),
                Now.AddDays(-age), Now.AddDays(-age), Now.AddDays(expiry), "IntegrityChecked",
                [new(new("source", "version", "tag", 1, Guid.NewGuid(), Guid.NewGuid()), obj.Name, 1, new string('B', 64))], []);
            return (obj, catalog);
        }
        public Task<IReadOnlyList<RetentionObject>> ListAsync(Guid installation, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RetentionObject>>(Items.ToArray());
        public Task<BackupCatalog> ReadCatalogAsync(RetentionObject item, CancellationToken cancellationToken) => Task.FromResult(Catalogs[item.Name]);
        public Task DeleteAsync(RetentionObject item, CancellationToken cancellationToken)
        {
            if (++_deleteAttempts == FailOnDeleteNumber) throw new IOException("Interrupted");
            if (DeleteError is not null) throw DeleteError;
            Items.Remove(item);
            Deleted.Add(item.Name);
            return Task.CompletedTask;
        }
    }
}
