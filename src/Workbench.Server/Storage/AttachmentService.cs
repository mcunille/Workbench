// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Authorization;
using Workbench.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Security;
using Workbench.Server.Operations;

namespace Workbench.Server.Storage;

public sealed record AttachmentRevisionInfo(Guid Id, long Length, string Sha256);

public sealed class AttachmentService(WorkbenchDbContext database, IBlobStore store, RequestActor actor)
{
    public const string ManagePermission = "attachments.manage";
    public const string ReadPermission = "attachments.read";

    // Internal server-generated content only. Untrusted ingestion requires a workflow
    // content policy and malware decision before this boundary can be exposed over HTTP.
    public async Task<AttachmentRevisionInfo> UploadAsync(Guid attachmentId, Guid operationId, Guid? expectedRevision,
        Stream content, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        if (attachmentId == Guid.Empty || operationId == Guid.Empty)
        {
            throw new ArgumentException("Attachment and operation identifiers are required.");
        }
        database.ChangeTracker.Clear();
        AttachmentRevision revision;
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            var attachment = await LockedAttachmentAsync(attachmentId, cancellationToken);
            var replay = await database.AttachmentRevisions.SingleOrDefaultAsync(
                row => row.OperationId == operationId, cancellationToken);
            if (replay is not null)
            {
                if (attachment?.DeletedAtUtc is null && replay.AttachmentId == attachmentId &&
                    replay.PreviousRevisionId == expectedRevision && replay.State == RevisionState.Available)
                {
                    return new AttachmentRevisionInfo(replay.Id, replay.Length!.Value, replay.Sha256!);
                }
                throw new DbUpdateConcurrencyException("The storage operation cannot be replayed.");
            }
            if (attachment is null && expectedRevision is null)
            {
                attachment = new Attachment { Id = attachmentId, TenantId = actor.TenantId, CreatedAtUtc = DateTimeOffset.UtcNow };
                database.Attachments.Add(attachment);
                await database.SaveChangesAsync(cancellationToken);
            }
            if (attachment is null || attachment.DeletedAtUtc is not null)
            {
                throw new FileNotFoundException("Attachment is unavailable.");
            }
            if (attachment.CurrentRevisionId != expectedRevision)
            {
                throw new DbUpdateConcurrencyException("The attachment changed.");
            }
            revision = new AttachmentRevision
            {
                Id = Guid.NewGuid(),
                TenantId = actor.TenantId,
                AttachmentId = attachmentId,
                OperationId = operationId,
                PreviousRevisionId = expectedRevision,
                ActorUserId = actor.UserId,
                ProviderAlias = store.Alias,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            database.AttachmentRevisions.Add(revision);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        var objectId = new BlobObjectId(actor.TenantId, revision.Id);
        try
        {
            var identity = await store.StageAsync(objectId, content, 25 * 1024 * 1024, cancellationToken);
            await store.PublishAsync(objectId, cancellationToken);
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var attachment = await LockedAttachmentAsync(attachmentId, cancellationToken);
            if (attachment is null || attachment.DeletedAtUtc is not null || attachment.CurrentRevisionId != expectedRevision)
            {
                throw new DbUpdateConcurrencyException("The attachment changed during upload.");
            }
            revision = await database.AttachmentRevisions.SingleAsync(row => row.Id == revision.Id, cancellationToken);
            revision.State = RevisionState.Available;
            revision.Length = identity.Length;
            revision.Sha256 = identity.Sha256;
            attachment.CurrentRevisionId = revision.Id;
            Audit("storage.revision.published", attachmentId);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AttachmentRevisionInfo(revision.Id, identity.Length, identity.Sha256);
        }
        catch
        {
            // Preserve provider bytes for reconciliation. Never erase an object after
            // an ambiguous SQL commit; the database may already reference it.
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<Stream> DownloadAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        Authorize(ReadPermission);
        var revision = await (from attachment in database.Attachments.AsNoTracking()
                              join item in database.AttachmentRevisions.AsNoTracking()
                              on attachment.CurrentRevisionId equals item.Id
                              where attachment.Id == attachmentId && attachment.DeletedAtUtc == null &&
                                  item.State == RevisionState.Available
                              select item).SingleOrDefaultAsync(cancellationToken);
        if (revision is null)
        {
            throw new FileNotFoundException("Attachment is unavailable.");
        }
        if (revision.ProviderAlias != store.Alias)
        {
            throw new IOException("The attachment provider is unavailable.");
        }
        return BlobIntegrity.Open(await store.OpenReadAsync(new BlobObjectId(actor.TenantId, revision.Id), cancellationToken),
            new BlobContentIdentity(revision.Length!.Value, revision.Sha256!));
    }

    public async Task DeleteAsync(Guid attachmentId, Guid expectedRevision, CancellationToken cancellationToken)
    {
        Authorize(ManagePermission);
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var attachment = await LockedAttachmentAsync(attachmentId, cancellationToken)
            ?? throw new FileNotFoundException("Attachment is unavailable.");
        if (attachment.CurrentRevisionId != expectedRevision)
        {
            throw new DbUpdateConcurrencyException("The attachment changed.");
        }
        if (attachment.DeletedAtUtc is not null)
        {
            return;
        }
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
        Audit("storage.attachment.deleted", attachmentId);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<Attachment?> LockedAttachmentAsync(Guid id, CancellationToken cancellationToken) =>
        database.Attachments.FromSqlInterpolated($"""
            SELECT * FROM [Storage].[Attachments] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}
            """).SingleOrDefaultAsync(cancellationToken);

    private void Authorize(string permission)
    {
        if (actor.UserId == Guid.Empty || actor.TenantId == Guid.Empty ||
            database.TenantContext.RequireTenantId() != actor.TenantId || !actor.Permissions.Contains(permission))
        {
            throw new UnauthorizedAccessException("Attachment access is denied.");
        }
    }

    private void Audit(string action, Guid attachmentId) => database.TenantSecurityAuditEvents.Add(new TenantSecurityAuditEvent
    {
        Id = Guid.NewGuid(),
        TenantId = actor.TenantId,
        ActorUserId = actor.UserId,
        Action = action,
        TargetType = "Attachment",
        TargetId = attachmentId,
        OccurredAtUtc = DateTimeOffset.UtcNow,
    });
}
