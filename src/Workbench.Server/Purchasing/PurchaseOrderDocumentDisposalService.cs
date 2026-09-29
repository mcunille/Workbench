// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Inventory;
using Workbench.Server.Persistence;

namespace Workbench.Server.Purchasing;

public sealed class PurchaseOrderDocumentDisposalService(WorkbenchDbContext database, RequestActor actor)
{
    public async Task<PurchaseOrderDocumentOperationResponse> DisposeAsync(Guid orderId, Guid documentId,
        DisposePurchaseOrderDocumentRequest request, CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new DocumentInputException(400, "Provide a request identifier and a disposal reason of 1 to 2,000 characters.");
        return await ExecuteAsync("Purchasing.DisposeRetainedDocument", orderId, request.RequestId, cancellationToken,
            new("@DocumentId", SqlDbType.UniqueIdentifier) { Value = documentId },
            new("@ExpectedOrderVersion", SqlDbType.Binary, 8) { Value = Version(request.ExpectedOrderVersion) },
            new("@ExpectedDocumentVersion", SqlDbType.Binary, 8) { Value = Version(request.ExpectedDocumentVersion) },
            new("@ExpectedEvidenceVersion", SqlDbType.Binary, 8) { Value = Version(request.ExpectedEvidenceVersion) },
            new("@Reason", SqlDbType.NVarChar, -1) { Value = request.Reason });
    }

    internal Task<PurchaseOrderDocumentOperationResponse> ReadOperationAsync(Guid orderId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync("Purchasing.ReadRetainedDocumentDisposal", orderId, requestId, ct);

    private async Task<PurchaseOrderDocumentOperationResponse> ExecuteAsync(string procedure, Guid orderId, Guid requestId, CancellationToken ct, params SqlParameter[] parameters)
    {
        if (actor.TenantId != database.TenantContext.RequireTenantId()) throw new UnauthorizedAccessException();
        await database.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new SqlCommand(procedure, (SqlConnection)database.Database.GetDbConnection()) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@ActorId", actor.UserId);
            command.Parameters.AddWithValue("@SessionId", actor.SessionId);
            command.Parameters.AddWithValue("@RequestId", requestId);
            command.Parameters.AddWithValue("@OrderId", orderId);
            command.Parameters.AddRange(parameters);
            var json = (string)(await command.ExecuteScalarAsync(ct))!;
            var result = JsonSerializer.Deserialize<PurchaseOrderDocumentOperationResponse>(json, JsonSerializerOptions.Web)!;
            return result with { OrderVersion = result.OrderVersion is null ? null : Convert.ToBase64String(Convert.FromHexString(result.OrderVersion.AsSpan(2))) };
        }
        catch (SqlException e) when (e.Number is 50903 or 51003 or 51000 or 51004 or 51009 or 51011 or 2601 or 2627)
        {
            throw new DocumentInputException(e.Number is 50903 or 51003 ? 403 : e.Number == 51000 ? 400 : e.Number == 51004 ? 404 : 409,
                e.Number is 2601 or 2627 ? "This request identifier is already in use." : e.Message,
                e.Number == 51011 ? "financial_evidence_retained" : null);
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }

    private static byte[] Version(string? value)
    {
        var bytes = new byte[8];
        if (value is null || !Convert.TryFromBase64String(value, bytes, out var count) || count != 8)
            throw new DocumentInputException(400, "Reload to obtain current versions.");
        return bytes;
    }
}
