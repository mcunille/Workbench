// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using Workbench.Server.Inventory;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Authorization;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;
namespace Workbench.Server.Purchasing;

public sealed class PurchaseOrderDocumentService(WorkbenchDbContext database, IBlobStore store, RequestActor actor)
{
    public async Task<DraftOrder> RequireContextAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (actor.UserId == Guid.Empty || actor.TenantId == Guid.Empty || actor.TenantId != database.TenantContext.RequireTenantId())
            throw new UnauthorizedAccessException("Document access is denied.");
        var order = await database.DraftOrders.AsNoTracking().SingleOrDefaultAsync(row => row.Id == orderId && !row.IsDeleted, cancellationToken)
            ?? throw new DocumentInputException(404, "Purchase order not found.");
        if (order.State != "Ordered") throw new DocumentInputException(409, "Record the purchase as ordered before adding invoice files.");
        return order;
    }
    private AttachmentService Attachments() => new(database, store, actor with
    {
        Permissions = new HashSet<string> { AttachmentService.ReadPermission, AttachmentService.ManagePermission },
    });
    public async Task<PurchaseOrderDocumentsResponse> ListAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Keep documents and their parent version in one stable SQL state.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var order = await RequireContextAsync(orderId, cancellationToken);
        var documents = await database.PurchaseOrderDocuments.AsNoTracking().Where(row => row.OrderId == orderId && row.RemovedAtUtc == null)
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id).ToArrayAsync(cancellationToken);
        var ids = documents.Select(row => row.RevisionId).ToArray();
        var unavailable = await database.Database.SqlQuery<Guid>($"SELECT RevisionId AS Value FROM Storage.RecoveryFiles")
            .Where(id => ids.Contains(id)).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(documents.Select(row => new PurchaseOrderDocumentResponse(row.Id, row.Label, row.MediaType, row.Extension, row.Length,
            row.CreatedAtUtc, Convert.ToBase64String(row.RowVersion), unavailable.Contains(row.RevisionId))).ToArray(),
            Convert.ToBase64String(order.RowVersion));
    }
    public async Task<PurchaseOrderDocumentOperationResponse> OperationAsync(Guid orderId, Guid requestId, CancellationToken cancellationToken)
    {
        await RequireContextAsync(orderId, cancellationToken);
        var operation = await database.PurchaseOrderDocumentOperations.AsNoTracking().SingleOrDefaultAsync(row => row.RequestId == requestId && row.OrderId == orderId, cancellationToken)
            ?? throw new DocumentInputException(404, "Operation not found.");
        return Result(operation);
    }
    public async Task<PurchaseOrderDocumentOperationResponse> ChangeAsync(Guid orderId, Guid requestId,
        byte[] expectedOrderVersion, Guid? documentId, byte[]? expectedDocumentVersion,
        string? label, byte[]? content, ValidatedDocument? validated, CancellationToken cancellationToken)
    {
        await RequireContextAsync(orderId, cancellationToken);
        label = label?.Trim();
        var kind = content is not null ? 0 : label is not null ? 1 : 2;
        if (content is not null && validated is null) throw new DocumentInputException(400, "Validate the document before saving it.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); cancellationToken = deadline.Token;
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqlConnection)database.Database.GetDbConnection();
        var resource = $"PurchaseOrderDocument:{actor.TenantId:N}:{requestId:N}";
        var acquired = false;
        try
        {
            // Serialize exact tenant request UUIDs across replicas without holding purchase order locks
            // during provider I/O. Durable SQL evidence survives a disconnected session.
            await using (var command = new SqlCommand("DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @result;", connection))
            {
                command.Parameters.AddWithValue("@resource", resource);
                acquired = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
            }
            if (!acquired) throw new DocumentInputException(503, "This document operation is still running. Retry shortly.");
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
            {
                await using var command = Procedure("Purchasing.PreparePurchaseOrderDocument");
                foreach (var (name, type, value) in new (string, SqlDbType, object?)[]
                {
                    ("@OrderId",SqlDbType.UniqueIdentifier,orderId),
                    ("@RequestId",SqlDbType.UniqueIdentifier,requestId),
                    ("@ExpectedOrderVersion",SqlDbType.VarBinary,expectedOrderVersion),
                    ("@DocumentId",SqlDbType.UniqueIdentifier,documentId),("@ExpectedDocumentVersion",SqlDbType.VarBinary,expectedDocumentVersion),
                    ("@Kind",SqlDbType.Int,kind),("@Label",SqlDbType.NVarChar,label),("@MediaType",SqlDbType.NVarChar,validated?.MediaType),
                    ("@Extension",SqlDbType.NVarChar,validated?.Extension),("@Length",SqlDbType.BigInt,content?.LongLength),
                    ("@Sha256",SqlDbType.NVarChar,content is null?null:Convert.ToHexString(SHA256.HashData(content))),
                    ("@ProviderAlias",SqlDbType.NVarChar,store.Alias),("@ActorUserId",SqlDbType.UniqueIdentifier,actor.UserId),
                })
                {
                    var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
                    if (type is SqlDbType.NVarChar or SqlDbType.VarBinary) parameter.Size = -1;
                    command.Parameters.Add(parameter);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            var operation = await LoadOperationAsync(requestId, cancellationToken);
            if (operation.State == 1) return Result(operation);
            if (operation.State == 2) throw Conflict();
            if (content is not null)
            {
                var revision = await database.AttachmentRevisions.AsNoTracking().SingleAsync(row => row.Id == operation.RevisionId, cancellationToken);
                await Attachments().PublishDocumentAsync(revision, content, cancellationToken);
            }
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
            {
                await using var command = Procedure("Purchasing.FinishPurchaseOrderDocument");
                command.Parameters.AddWithValue("@RequestId", requestId);
                command.Parameters.AddWithValue("@Published", content is not null);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            operation = await LoadOperationAsync(requestId, cancellationToken);
            if (operation.State == 2) throw Conflict();
            return Result(operation);
        }
        catch (SqlException error) when (error.Number is 50076 or 50077 or 50078 or 50079 or 50403 or 2601 or 2627)
        {
            throw new DocumentInputException(error.Number == 50403 ? 403 : error.Number == 50076 ? 400 : error.Number == 50078 ? 404 : 409,
                error.Number is 2601 or 2627 ? "This request identifier was already used. Resolve its outcome before retrying." : error.Message);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    await using var release = new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';", connection) { CommandTimeout = 5 };
                    release.Parameters.AddWithValue("@resource", resource);
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception error) when (error is SqlException or InvalidOperationException) { SqlConnection.ClearPool(connection); }
            }
            await database.Database.CloseConnectionAsync();
        }
    }
    public async Task<(byte[] Content, string MediaType, string Extension)> ReadAsync(Guid orderId, Guid documentId, CancellationToken cancellationToken)
    {
        await RequireContextAsync(orderId, cancellationToken);
        var document = await database.PurchaseOrderDocuments.AsNoTracking().SingleOrDefaultAsync(row => row.OrderId == orderId && row.Id == documentId && row.RemovedAtUtc == null, cancellationToken)
            ?? throw new DocumentInputException(404, "Document not found.");
        await using var source = await Attachments().DownloadAsync(document.AttachmentId, cancellationToken);
        using var bytes = new MemoryStream();
        await BlobTransfer.CopyAsync(source, bytes, 10 * 1024 * 1024, cancellationToken);
        // AttachmentService verifies the complete revision. Also bind to the immutable document
        // evidence, so a pointer change cannot silently serve different paperwork.
        var content = bytes.ToArray();
        if (content.LongLength != document.Length || Convert.ToHexString(SHA256.HashData(content)) != document.Sha256)
            throw new IOException("The stored document failed integrity verification.");
        return (content, document.MediaType, document.Extension);
    }
    private SqlCommand Procedure(string name) => new(name, (SqlConnection)database.Database.GetDbConnection(),
        (SqlTransaction)database.Database.CurrentTransaction!.GetDbTransaction())
    { CommandType = CommandType.StoredProcedure };
    private Task<PurchaseOrderDocumentOperation> LoadOperationAsync(Guid requestId, CancellationToken cancellationToken) =>
        database.PurchaseOrderDocumentOperations.AsNoTracking().SingleAsync(row => row.RequestId == requestId, cancellationToken);
    private static DocumentInputException Conflict() => new(409, "The purchase order or document changed. Reload before trying again.");
    private static PurchaseOrderDocumentOperationResponse Result(PurchaseOrderDocumentOperation operation) => new(operation.RequestId,
        operation.State switch { 0 => "Pending", 1 => "Completed", _ => "Conflict" }, operation.DocumentId,
        operation.ResultOrderVersion is null ? null : Convert.ToBase64String(operation.ResultOrderVersion));
}
