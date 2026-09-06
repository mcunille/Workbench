// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.CompilerServices;

namespace Workbench.Server.Storage;

public sealed class FileSystemBlobStore(string root, string alias = "filesystem") : IBlobStore
{
    public string Alias => alias;
    public async Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new ConfinedDirectory(root);
        if (directory.Exists(Name(id, published: true)) || directory.Exists(Name(id, published: false)))
        {
            throw new IOException("Storage object already exists.");
        }
        var pending = $"{id.TenantId:N}-{id.RevisionId:N}.c";
        // Exclusive creation occurs outside cleanup: a competing writer owns its own file.
        var destination = directory.Open(pending, create: true);
        try
        {
            BlobContentIdentity identity;
            await using (destination)
            {
                identity = await BlobTransfer.CopyAsync(content, destination, maximumBytes, cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            directory.Publish(pending, Name(id, published: false));
            return identity;
        }
        catch
        {
            directory.Delete(pending);
            throw;
        }
    }

    public Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new ConfinedDirectory(root);
        if (!directory.Exists(Name(id, published: true)))
        {
            directory.Publish(Name(id, published: false), Name(id, published: true));
        }
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new ConfinedDirectory(root);
        return Task.FromResult<Stream>(directory.Open(Name(id, published: true), create: false));
    }

    public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new ConfinedDirectory(root);
        directory.Delete(Name(id, published: true));
        directory.Delete(Name(id, published: false));
        directory.Delete($"{id.TenantId:N}-{id.RevisionId:N}.c");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BlobObjectId> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var directory = new ConfinedDirectory(root);
        foreach (var name in directory.Names())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.Length == 67 && name[32] == '-' && name[65] == '.' && name[66] is 'a' or 'b' or 'c' &&
                Guid.TryParseExact(name[..32], "N", out var tenant) && tenant != Guid.Empty &&
                Guid.TryParseExact(name[33..65], "N", out var revision) && revision != Guid.Empty)
            {
                yield return new BlobObjectId(tenant, revision);
            }
        }
        await Task.CompletedTask;
    }

    public Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = new ConfinedDirectory(root);
        return Task.CompletedTask;
    }

    private static string Name(BlobObjectId id, bool published) =>
        $"{id.TenantId:N}-{id.RevisionId:N}.{(published ? 'b' : 'a')}";
}
