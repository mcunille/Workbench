// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Purchasing;

// Consumes an already-open tenant-proof-scoped connection. SQL rechecks actor/session authority.
internal sealed class SupplierBillQueries(SqlConnection connection)
{
    internal Task<SupplierBillPage> ListAsync(Guid actorId, Guid sessionId, Guid purchaseOrderId, Guid? afterId, int take, CancellationToken cancellationToken)
        => ReadAsync<SupplierBillPage>("Purchasing.ReadSupplierBills", actorId, sessionId, cancellationToken,
            new SqlParameter("@PurchaseOrderId", SqlDbType.UniqueIdentifier) { Value = purchaseOrderId },
            new SqlParameter("@AfterId", SqlDbType.UniqueIdentifier) { Value = (object?)afterId ?? DBNull.Value }, new SqlParameter("@Take", SqlDbType.Int) { Value = take });

    internal Task<SupplierBillDetail> ReadAsync(Guid actorId, Guid sessionId, Guid billId, CancellationToken cancellationToken)
        => ReadAsync<SupplierBillDetail>("Purchasing.ReadSupplierBill", actorId, sessionId, cancellationToken,
            new SqlParameter("@BillId", SqlDbType.UniqueIdentifier) { Value = billId });

    internal Task<SupplierBillHistoryPage> HistoryAsync(Guid actorId, Guid sessionId, Guid billId, long afterSequence, int take, CancellationToken cancellationToken)
        => ReadAsync<SupplierBillHistoryPage>("Purchasing.ReadSupplierBillHistory", actorId, sessionId, cancellationToken,
            new SqlParameter("@BillId", SqlDbType.UniqueIdentifier) { Value = billId }, new SqlParameter("@AfterSequence", SqlDbType.BigInt) { Value = afterSequence },
            new SqlParameter("@Take", SqlDbType.Int) { Value = take });

    private async Task<T> ReadAsync<T>(string procedure, Guid actorId, Guid sessionId, CancellationToken cancellationToken, params SqlParameter[] parameters)
    {
        using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@ActorId", SqlDbType.UniqueIdentifier).Value = actorId;
        command.Parameters.Add("@SessionId", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.AddRange(parameters);
        var json = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("Bill query returned no result.");
    }
}
