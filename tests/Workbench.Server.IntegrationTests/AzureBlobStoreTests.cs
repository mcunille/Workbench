// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Azure.Storage;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class AzureBlobStoreTests
{
    [Fact]
    public async Task AzurePreservesImmutableContentAndInstallationIsolation()
    {
        // GIVEN a disposable emulator with a per-test account key and private container.
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var emulator = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
            .WithEnvironment("AZURITE_ACCOUNTS", "testaccount:" + key)
            .WithPortBinding(10000, assignRandomHostPort: true)
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--silent")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10000)).Build();
        await emulator.StartAsync();
        var options = new BlobClientOptions();
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(
            new Uri($"http://{emulator.Hostname}:{emulator.GetMappedPublicPort(10000)}/testaccount/blobs"),
            new StorageSharedKeyCredential("testaccount", key), options);
        await container.CreateAsync();
        var store = new AzureBlobStore(container, Guid.NewGuid());
        var id = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
        using var content = new MemoryStream([1, 2, 3]);
        // WHEN bytes are staged and then published.
        var identity = await store.StageAsync(id, content, 10, CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(id, CancellationToken.None));
        await store.PublishAsync(id, CancellationToken.None);
        await store.PublishAsync(id, CancellationToken.None);
        // THEN publication is idempotent and another installation or tenant cannot read the object.
        Assert.Equal(3, identity.Length);
        await using (var read = await store.OpenReadAsync(id, CancellationToken.None))
        {
            Assert.Equal(1, read.ReadByte());
        }
        using var replacement = new MemoryStream([4]);
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(id, replacement, 10, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new AzureBlobStore(container, Guid.NewGuid()).OpenReadAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.OpenReadAsync(new BlobObjectId(Guid.NewGuid(), id.RevisionId), CancellationToken.None));
        // WHEN content migrates to filesystem and back to a second installation.
        var root = Path.Combine(Path.GetTempPath(), "workbench-copy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filesystem = new FileSystemBlobStore(root);
            var entry = new BlobManifestEntry(id.TenantId, id.RevisionId, store.Alias, identity.Length, identity.Sha256);
            await BlobMaintenance.CopyAsync(store, filesystem, entry, CancellationToken.None);
            await BlobMaintenance.CopyAsync(store, filesystem, entry, CancellationToken.None);
            var migrated = new AzureBlobStore(container, Guid.NewGuid());
            await BlobMaintenance.CopyAsync(filesystem, migrated, entry, CancellationToken.None);
            // THEN each destination verifies against SQL-derived content identity, including on resume.
            await BlobMaintenance.VerifyAsync(migrated, entry, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => BlobMaintenance.VerifyAsync(filesystem,
                entry with { Sha256 = new string('0', 64) }, CancellationToken.None));
            // GIVEN an operator accidentally enables anonymous access on the destination.
            await container.SetAccessPolicyAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob);
            var publicTarget = new AzureBlobStore(container, Guid.NewGuid());
            // WHEN maintenance attempts to copy private retained content.
            await Assert.ThrowsAsync<IOException>(() => BlobMaintenance.CopyAsync(filesystem,
                publicTarget, entry, CancellationToken.None));
            // THEN no destination content is published.
            await Assert.ThrowsAsync<FileNotFoundException>(() => publicTarget.OpenReadAsync(id, CancellationToken.None));
            await container.SetAccessPolicyAsync(Azure.Storage.Blobs.Models.PublicAccessType.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        // AND an oversized upload cannot become readable.
        var rejected = new BlobObjectId(id.TenantId, Guid.NewGuid());
        using var oversized = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StageAsync(rejected, oversized, 2, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.PublishAsync(rejected, CancellationToken.None));
        await store.DeleteAsync(id, CancellationToken.None);
        await store.DeleteAsync(id, CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(id, CancellationToken.None));
    }
}
