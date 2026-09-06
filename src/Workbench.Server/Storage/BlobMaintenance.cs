// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Storage;

public sealed record BlobManifestEntry(Guid TenantId, Guid RevisionId, string ProviderAlias, long Length, string Sha256);

public static class BlobMaintenance
{
    public static async Task VerifyAsync(IBlobStore store, BlobManifestEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Sha256.Length != 64)
        {
            throw new InvalidDataException("Invalid content manifest.");
        }
        await using var stream = await store.OpenReadAsync(new BlobObjectId(entry.TenantId, entry.RevisionId), cancellationToken);
        var actual = await BlobTransfer.CopyAsync(stream, Stream.Null, Math.Max(1, entry.Length), cancellationToken);
        if (actual.Length != entry.Length || actual.Sha256 != entry.Sha256)
        {
            throw new InvalidDataException("Blob content does not match authoritative metadata.");
        }
    }

    public static async Task CopyAsync(IBlobStore source, IBlobStore target, BlobManifestEntry entry, CancellationToken cancellationToken)
    {
        await target.CheckReadyAsync(cancellationToken);
        await VerifyAsync(source, entry, cancellationToken);
        try
        {
            await VerifyAsync(target, entry, cancellationToken);
            return;
        }
        catch (FileNotFoundException) { }
        var id = new BlobObjectId(entry.TenantId, entry.RevisionId);
        await using var sourceStream = await source.OpenReadAsync(id, cancellationToken);
        try
        {
            var identity = await target.StageAsync(id, sourceStream, Math.Max(1, entry.Length), cancellationToken);
            if (identity.Length != entry.Length || identity.Sha256 != entry.Sha256)
            {
                throw new InvalidDataException("Source content changed during migration.");
            }
        }
        catch (IOException)
        {
            // A previous attempt may have committed staging before losing its
            // acknowledgement. Publication is create-only and verification remains mandatory.
            await target.PublishAsync(id, cancellationToken);
            await VerifyAsync(target, entry, cancellationToken);
            return;
        }
        await target.PublishAsync(id, cancellationToken);
        await VerifyAsync(target, entry, cancellationToken);
    }
}
