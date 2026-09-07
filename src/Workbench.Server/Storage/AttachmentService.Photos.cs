// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Operations;

namespace Workbench.Server.Storage;

public sealed partial class AttachmentService
{
    // Only the item workflow may compose these operations. The caller owns the SQL transaction
    // and has resolved inventory authority; these methods retain the attachment permission check.
    internal AttachmentRevision PreparePhoto(Guid attachmentId)
    {
        Authorize(ManagePermission);
        var now = DateTimeOffset.UtcNow;
        database.Attachments.Add(new Attachment { Id = attachmentId, TenantId = actor.TenantId, CreatedAtUtc = now });
        var revision = new AttachmentRevision
        {
            Id = Guid.NewGuid(),
            TenantId = actor.TenantId,
            AttachmentId = attachmentId,
            OperationId = Guid.NewGuid(),
            ActorUserId = actor.UserId,
            ProviderAlias = store.Alias,
            Source = "ItemPhotoNormalized",
            MediaType = "image/webp",
            CreatedAtUtc = now,
        };
        database.AttachmentRevisions.Add(revision);
        return revision;
    }

    internal async Task<BlobContentIdentity> PublishPhotoAsync(AttachmentRevision revision, byte[] bytes, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        if (revision.State != RevisionState.Pending || revision.ProviderAlias != store.Alias)
            throw new IOException("The pending photo provider is unavailable.");
        var id = new BlobObjectId(actor.TenantId, revision.Id);
        var expected = new BlobContentIdentity(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        using var content = new MemoryStream(bytes, writable: false);
        try
        {
            await store.StageAsync(id, content, 10 * 1024 * 1024, cancellationToken);
        }
        catch (IOException)
        {
            // A previous attempt can already have staged/published this exact immutable revision.
            // Publication plus full digest verification must succeed before it can be referenced.
        }
        await store.PublishAsync(id, cancellationToken);
        await using var verified = BlobIntegrity.Open(await store.OpenReadAsync(id, cancellationToken), expected);
        await verified.CopyToAsync(Stream.Null, cancellationToken);
        return expected;
    }

    internal async Task CompletePhotoAsync(AttachmentRevision revision, BlobContentIdentity identity, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        RequirePhotoTransaction();
        revision.State = RevisionState.Available;
        revision.Length = identity.Length;
        revision.Sha256 = identity.Sha256;
        var attachment = await database.Attachments.SingleAsync(row => row.Id == revision.AttachmentId, cancellationToken);
        attachment.CurrentRevisionId = revision.Id;
    }

    internal async Task RetirePhotoAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        RequirePhotoTransaction();
        var attachment = await database.Attachments.SingleAsync(row => row.Id == attachmentId, cancellationToken);
        if (attachment.DeletedAtUtc is not null)
            return;
        var now = DateTimeOffset.UtcNow;
        attachment.DeletedAtUtc = now;
        attachment.DeleteAfterUtc = now.AddDays(7);
        database.WorkItems.Add(new WorkItem
        {
            Id = Guid.NewGuid(),
            TenantId = actor.TenantId,
            Kind = WorkKind.DeleteAttachment,
            AttachmentId = attachmentId,
            CreatedAtUtc = now,
            AvailableAtUtc = attachment.DeleteAfterUtc.Value,
        });
    }

    private void RequirePhotoTransaction()
    {
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Photo finalization requires the caller's transaction.");
    }
}
