// Copyright (c) 2026 The White Stag Collection.
using System.Security.Cryptography;
namespace Workbench.Server.Storage;

public sealed partial class AttachmentService
{
    internal async Task PublishDocumentAsync(AttachmentRevision revision, byte[] bytes, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        if (revision.State != RevisionState.Pending || revision.ProviderAlias != store.Alias)
            throw new IOException("The pending document provider is unavailable.");
        var id = new BlobObjectId(actor.TenantId, revision.Id);
        var expected = new BlobContentIdentity(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        using var content = new MemoryStream(bytes, writable: false);
        try { await store.StageAsync(id, content, 10 * 1024 * 1024, cancellationToken); }
        catch (IOException)
        {
            // A prior attempt may already have staged or published this immutable identity.
            // Only successful publication and complete digest verification permit finalization.
        }
        await store.PublishAsync(id, cancellationToken);
        await using var verified = BlobIntegrity.Open(await store.OpenReadAsync(id, cancellationToken), expected);
        await verified.CopyToAsync(Stream.Null, cancellationToken);
    }
}
