// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-blob-test-" + Guid.NewGuid().ToString("N"));

    public FileSystemBlobStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StagingIsInvisibleAndPublicationCannotOverwrite()
    {
        // GIVEN staged content in an isolated storage root.
        var store = new FileSystemBlobStore(_root);
        var id = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
        using var first = new MemoryStream([1, 2, 3]);
        await store.StageAsync(id, first, 10, CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(id, CancellationToken.None));
        // AND offline inventory exposes the stage so abandoned uploads can be reconciled.
        var inventory = new List<BlobObjectId>();
        await foreach (var item in store.ListAsync(CancellationToken.None)) { inventory.Add(item); }
        Assert.Contains(id, inventory);
        // WHEN publication completes and is retried.
        await store.PublishAsync(id, CancellationToken.None);
        await store.PublishAsync(id, CancellationToken.None);
        // THEN bytes are stable and another stage cannot overwrite the immutable object.
        using var replacement = new MemoryStream([4, 5]);
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(id, replacement, 10, CancellationToken.None));
        await using var read = await store.OpenReadAsync(id, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
    }

    [Fact]
    public async Task IncompleteStageCannotBeSelectedForPublication()
    {
        // GIVEN a writer paused before completing its upload.
        var store = new FileSystemBlobStore(_root);
        var id = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new PausedStream(entered, resume);
        var staging = store.StageAsync(id, content, 10, CancellationToken.None);
        await entered.Task;
        try
        {
            // WHEN another operation looks for a completed stage, THEN none exists yet.
            Assert.False(File.Exists(Path.Combine(_root, $"{id.TenantId:N}-{id.RevisionId:N}.a")));
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.PublishAsync(id, CancellationToken.None));
        }
        finally
        {
            resume.SetResult();
            await staging;
        }
        // AND the completed upload can subsequently be published.
        await store.PublishAsync(id, CancellationToken.None);
    }

    private sealed class PausedStream(TaskCompletionSource entered, TaskCompletionSource resume) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task TenantIdentifierSelectsADifferentObject()
    {
        // GIVEN a published object owned by one tenant.
        var store = new FileSystemBlobStore(_root);
        var id = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
        using var content = new MemoryStream([42]);
        await store.StageAsync(id, content, 10, CancellationToken.None);
        await store.PublishAsync(id, CancellationToken.None);
        // WHEN the revision identifier is substituted under another tenant, THEN no bytes are returned.
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(
            new BlobObjectId(Guid.NewGuid(), id.RevisionId), CancellationToken.None));
    }

    [Fact]
    public async Task OversizeUploadCannotBePublished()
    {
        // GIVEN content exceeding its size limit.
        var store = new FileSystemBlobStore(_root);
        var id = new BlobObjectId(Guid.NewGuid(), Guid.NewGuid());
        using var content = new MemoryStream([1, 2, 3]);
        // WHEN staging fails, THEN partial content cannot be published.
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StageAsync(id, content, 2, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.PublishAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task LinkedStorageRootIsRejected()
    {
        // GIVEN a root which is a directory link to another directory.
        var link = Path.Combine(_root, "linked-root");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:WORKBENCH_TEST_LINK -Target $env:WORKBENCH_TEST_TARGET | Out-Null");
            start.Environment["WORKBENCH_TEST_LINK"] = link;
            start.Environment["WORKBENCH_TEST_TARGET"] = _root;
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, _root);
        }
        try
        {
            var store = new FileSystemBlobStore(link);
            // WHEN readiness is checked, THEN the provider refuses the linked root.
            await Assert.ThrowsAsync<IOException>(() => store.CheckReadyAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
