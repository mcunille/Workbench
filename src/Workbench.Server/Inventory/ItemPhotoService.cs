// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Authorization;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

public sealed class ItemPhotoService(WorkbenchDbContext database, IBlobStore store, RequestActor actor, PhotoProcessor processor)
{
    public async Task<InventoryItem> RequireItemAsync(Guid id, CancellationToken cancellationToken)
    {
        if (actor.UserId == Guid.Empty || actor.TenantId == Guid.Empty || actor.TenantId != database.TenantContext.RequireTenantId())
            throw new UnauthorizedAccessException("Photo access is denied.");
        return await database.Items.AsNoTracking().Include(row => row.CurrentPhoto)
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new PhotoInputException(404, "Item not found.");
    }

    // H1 inventory authority is authenticated tenant membership. Map it to a narrowly scoped
    // attachment capability only after RequireItemAsync, without expanding generic endpoints.
    private AttachmentService Attachments() => new(database, store, actor with
    {
        Permissions = new HashSet<string> { AttachmentService.ReadPermission, AttachmentService.ManagePermission },
    });

    public async Task<ItemPhotoMutationResponse> ChangeAsync(Guid id, Guid requestId, byte[] expectedVersion,
        byte[]? content, CancellationToken cancellationToken)
    {
        await RequireItemAsync(id, cancellationToken);
        if (requestId == Guid.Empty || expectedVersion.Length != 8)
            throw new PhotoInputException(400, "A request identifier and the current item version are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        var kind = content is null ? PhotoOperationKind.Remove : PhotoOperationKind.Upload;
        var hash = Convert.ToHexString(SHA256.HashData(content ?? []));
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqlConnection)database.Database.GetDbConnection();
        var lockName = $"ItemPhoto:{actor.TenantId:N}:{id:N}:{requestId:N}";
        var acquired = false;
        try
        {
            // A session lock serializes exact retries across replicas without holding an item row
            // lock during I/O. Durable pending rows survive disconnects and can be resumed.
            await using (var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive',
                    @LockOwner='Session', @LockTimeout=0;
                SELECT @result;
                """, connection))
            {
                command.Parameters.AddWithValue("@resource", lockName);
                acquired = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
            }
            if (!acquired)
                throw new PhotoInputException(503, "This photo operation is still running. Retry shortly.");
            database.ChangeTracker.Clear();
            var operation = await database.ItemPhotoOperations.SingleOrDefaultAsync(
                row => row.ItemId == id && row.RequestId == requestId, cancellationToken);
            if (operation is not null)
            {
                if (operation.Kind != kind || operation.PayloadSha256 != hash || !operation.ExpectedVersion.SequenceEqual(expectedVersion))
                    throw new PhotoInputException(409, "This request identifier was used for a different photo operation.");
                if (operation.State == PhotoOperationState.Completed)
                    return Result(operation);
                if (operation.State == PhotoOperationState.Conflict)
                    throw Conflict();
            }
            var item = await RequireItemAsync(id, cancellationToken);
            if (item.ArchivedAtUtc is not null)
                throw Archived();
            if (!item.RowVersion.SequenceEqual(expectedVersion))
                throw Conflict();
            var images = content is null ? null : processor.Process(content);
            var attachments = Attachments();
            if (operation is null)
            {
                await using var preparation = await database.Database.BeginTransactionAsync(cancellationToken);
                operation = new ItemPhotoOperation
                {
                    Id = Guid.NewGuid(),
                    TenantId = actor.TenantId,
                    ItemId = id,
                    RequestId = requestId,
                    ExpectedVersion = expectedVersion,
                    PayloadSha256 = hash,
                    Kind = kind,
                    ActorUserId = actor.UserId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                if (images is not null)
                {
                    operation.DetailAttachmentId = Guid.NewGuid();
                    operation.ThumbnailAttachmentId = Guid.NewGuid();
                    attachments.PreparePhoto(operation.DetailAttachmentId.Value);
                    attachments.PreparePhoto(operation.ThumbnailAttachmentId.Value);
                }
                database.ItemPhotoOperations.Add(operation);
                await database.SaveChangesAsync(cancellationToken);
                await preparation.CommitAsync(cancellationToken);
            }
            AttachmentRevision? detail = null, thumbnail = null;
            BlobContentIdentity? detailIdentity = null, thumbnailIdentity = null;
            if (images is not null)
            {
                detail = await database.AttachmentRevisions.SingleAsync(row => row.AttachmentId == operation.DetailAttachmentId, cancellationToken);
                thumbnail = await database.AttachmentRevisions.SingleAsync(row => row.AttachmentId == operation.ThumbnailAttachmentId, cancellationToken);
                detailIdentity = await attachments.PublishPhotoAsync(detail, images.Detail, cancellationToken);
                thumbnailIdentity = await attachments.PublishPhotoAsync(thumbnail, images.Thumbnail, cancellationToken);
            }
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
            {
                if (images is not null)
                {
                    await attachments.CompletePhotoAsync(detail!, detailIdentity!, cancellationToken);
                    await attachments.CompletePhotoAsync(thumbnail!, thumbnailIdentity!, cancellationToken);
                    database.ItemPhotos.Add(new ItemPhoto
                    {
                        Id = operation.Id,
                        TenantId = actor.TenantId,
                        ItemId = id,
                        OperationId = operation.Id,
                        DetailAttachmentId = operation.DetailAttachmentId!.Value,
                        ThumbnailAttachmentId = operation.ThumbnailAttachmentId!.Value,
                        Width = images.Width,
                        Height = images.Height,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                    });
                    await database.SaveChangesAsync(cancellationToken);
                }
                try
                {
                    operation.ResultVersion = await SetCurrentAsync(id, expectedVersion, images is null ? null : operation.Id, cancellationToken);
                }
                catch (SqlException error) when (error.Number == 50040)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    await RetainConflictAsync(operation.Id, detailIdentity, thumbnailIdentity, cancellationToken);
                    if ((await RequireItemAsync(id, cancellationToken)).ArchivedAtUtc is not null)
                        throw Archived();
                    throw Conflict();
                }
                if (item.CurrentPhoto is { } previous)
                {
                    await attachments.RetirePhotoAsync(previous.DetailAttachmentId, cancellationToken);
                    await attachments.RetirePhotoAsync(previous.ThumbnailAttachmentId, cancellationToken);
                }
                operation.State = PhotoOperationState.Completed;
                database.TenantSecurityAuditEvents.Add(new TenantSecurityAuditEvent
                {
                    Id = Guid.NewGuid(),
                    TenantId = actor.TenantId,
                    ActorUserId = actor.UserId,
                    TargetType = "Item",
                    TargetId = id,
                    Action = images is null ? "inventory.photo.removed" : "inventory.photo.saved",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return Result(operation);
        }
        finally
        {
            database.ChangeTracker.Clear();
            if (acquired)
            {
                try
                {
                    await using var release = new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';", connection)
                    { CommandTimeout = 5 };
                    release.Parameters.AddWithValue("@resource", lockName);
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception error) when (error is SqlException or InvalidOperationException)
                {
                    // Never return a connection carrying a session lock to the pool.
                    SqlConnection.ClearPool(connection);
                }
            }
            await database.Database.CloseConnectionAsync();
        }
    }

    private async Task<byte[]> SetCurrentAsync(Guid id, byte[] expectedVersion, Guid? photoId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("[Inventory].[SetItemPhoto]", (SqlConnection)database.Database.GetDbConnection(),
            (SqlTransaction)database.Database.CurrentTransaction!.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ExpectedVersion", expectedVersion);
        command.Parameters.Add(new SqlParameter("@PhotoId", SqlDbType.UniqueIdentifier) { Value = (object?)photoId ?? DBNull.Value });
        return (byte[])(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task RetainConflictAsync(Guid operationId, BlobContentIdentity? detailIdentity,
        BlobContentIdentity? thumbnailIdentity, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var retention = await database.Database.BeginTransactionAsync(cancellationToken);
        var operation = await database.ItemPhotoOperations.SingleAsync(row => row.Id == operationId, cancellationToken);
        var attachments = Attachments();
        if (detailIdentity is not null && thumbnailIdentity is not null)
        {
            foreach (var (attachmentId, identity) in new[]
            {
                (operation.DetailAttachmentId!.Value, detailIdentity), (operation.ThumbnailAttachmentId!.Value, thumbnailIdentity),
            })
            {
                var revision = await database.AttachmentRevisions.SingleAsync(row => row.AttachmentId == attachmentId, cancellationToken);
                await attachments.CompletePhotoAsync(revision, identity, cancellationToken);
                await attachments.RetirePhotoAsync(attachmentId, cancellationToken);
            }
        }
        operation.State = PhotoOperationState.Conflict;
        await database.SaveChangesAsync(cancellationToken);
        await retention.CommitAsync(cancellationToken);
    }

    public async Task<byte[]> ReadAsync(Guid id, Guid photoId, string variant, CancellationToken cancellationToken)
    {
        var item = await RequireItemAsync(id, cancellationToken);
        if (item.CurrentPhoto is not { } photo || photo.Id != photoId || variant is not ("thumbnail" or "detail"))
            throw new PhotoInputException(404, "Photo not found.");
        await using var stream = await Attachments().DownloadAsync(
            variant == "thumbnail" ? photo.ThumbnailAttachmentId : photo.DetailAttachmentId, cancellationToken);
        using var bytes = new MemoryStream();
        await BlobTransfer.CopyAsync(stream, bytes, 10 * 1024 * 1024, cancellationToken);
        return bytes.ToArray();
    }

    private static PhotoInputException Conflict() => new(409, "The item changed in another session. Reload it before changing its photo.");
    private static PhotoInputException Archived() => new(409, "This record is archived and cannot be changed.", "item_archived");
    private static ItemPhotoMutationResponse Result(ItemPhotoOperation operation) => new(operation.RequestId,
        Convert.ToBase64String(operation.ResultVersion!), operation.Kind == PhotoOperationKind.Upload ? operation.Id : null);
}
