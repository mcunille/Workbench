// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillEvidenceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task NonAbandonedBillLocksSupplierIdentity()
    {
        // GIVEN a bill for an ordered purchase without recognition events yet.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        await context.AdminAsync("UPDATE Purchasing.DraftOrders SET PoNumber=1");
        context.Recognition.PurchaseOrderVersion = await context.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.DraftOrders");
        var saved = await context.SaveAsync(Guid.NewGuid(), context.DraftCommand());
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with
        {
            SupplierId = null,
            SupplierName = "Replacement supplier",
            Notes = "Operational amendment",
            Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }]
        });
        // WHEN the operational amendment would replace its supplier.
        await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", context.Journal.Connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid());
        command.Parameters.AddWithValue("@ActorUserId", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@TargetId", context.Recognition.PurchaseOrderId);
        command.Parameters.AddWithValue("@ExpectedVersion", Convert.FromHexString(context.Recognition.PurchaseOrderVersion[2..]));
        command.Parameters.AddWithValue("@Operation", "Amend"); command.Parameters.AddWithValue("@OrderDate", "2026-01-01");
        command.Parameters.AddWithValue("@Reason", "Operational amendment");
        command.Parameters.AddWithValue("@Draft", JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions));
        command.Parameters.AddWithValue("@Calculation", "{}");
        // THEN the bill source prevents reassignment; abandoning the unposted bill releases that restriction.
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        await context.SaveAsync(Guid.NewGuid(), context.Change(saved, "Abandon"));
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task UnknownDocumentCannotBeLinkedByCallerClaims()
    {
        // GIVEN a completed draft that names an unavailable private document revision.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = new JsonArray(new JsonObject
        { ["documentId"] = Guid.NewGuid().ToString(), ["revisionId"] = Guid.NewGuid().ToString() });
        // WHEN a draft tries to link it THEN no new source is committed.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), draft))).Number);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBills"));
    }
}
