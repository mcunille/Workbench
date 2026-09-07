// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class FileRecoveryTests
{
    private static readonly RecoveryRevision Revision = new(Guid.NewGuid(), Guid.NewGuid(), "production", 3,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])), 1, "rowversion");

    [Fact]
    public async Task MissingContentPreservesSqlReferenceAndIdentifiesOnlyUnreferencedObjects()
    {
        // GIVEN an isolated recovered store with one missing SQL reference and an extra blob.
        var store = new Store { Error = new FileNotFoundException() };
        var inventory = new RecoveryInventory("Workbench", "isolated", 1, [Revision]);
        // WHEN reconciliation inspects without changing SQL or deleting objects.
        var report = await FileRecovery.InspectAsync(inventory, JsonSerializer.Serialize(inventory), store, Guid.NewGuid(), default);
        // THEN the SQL reference is a missing-file disposition, and only the blob-only object is an orphan.
        Assert.Equal(new MissingRecoveryFile(Revision.TenantId, Revision.RevisionId, "Missing"), Assert.Single(report.Missing));
        Assert.Equal(store.Orphan, Assert.Single(report.Orphans));
    }

    [Theory]
    [InlineData("production", 1)]
    [InlineData("isolated", 0)]
    public async Task ProductionBindingAndPendingSqlOperationsBlockReconciliation(string alias, int state)
    {
        // GIVEN either the source binding or unresolved pending work in SQL.
        var inventory = new RecoveryInventory("Workbench", "isolated", 1, [Revision with { State = state }]);
        // WHEN reconciliation is requested.
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileRecovery.InspectAsync(inventory,
            JsonSerializer.Serialize(inventory), new Store { Alias = alias }, Guid.NewGuid(), default));
        // THEN no report can authorize recovery cleanup.
    }

    [Fact]
    public async Task ProviderFailureCannotBeAcceptedAsMissing()
    {
        // GIVEN a provider access error rather than verified absence.
        var inventory = new RecoveryInventory("Workbench", "isolated", 1, [Revision]);
        // WHEN the file is inspected.
        await Assert.ThrowsAsync<IOException>(() => FileRecovery.InspectAsync(inventory, JsonSerializer.Serialize(inventory),
            new Store { Error = new IOException("Provider denied access.") }, Guid.NewGuid(), default));
        // THEN inspection fails rather than producing an acceptable loss report.
    }

    private sealed class Store : IBlobStore
    {
        public string Alias { get; init; } = "isolated";
        public Exception? Error { get; init; }
        public BlobObjectId Orphan { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public Task CheckReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken) =>
            Error is null ? Task.FromResult<Stream>(new MemoryStream([1, 2, 3])) : Task.FromException<Stream>(Error);
        public async IAsyncEnumerable<BlobObjectId> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.Yield(); yield return Orphan; }
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
