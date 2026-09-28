// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillCorrectionGuardTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task GenericRecognitionCorrectionCannotDetachPostedBill()
    {
        // GIVEN a posted bill and a trusted synthetic caller of the generic correction kernel.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft(); var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        await context.AdminAsync("""
            CREATE PROCEDURE Purchasing.CorrectBillUnitForTest @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            AS BEGIN BEGIN TRANSACTION;
              EXEC Purchasing.CorrectRecognition @ActorId,@SessionId,@RequestId,N'SupplierBillsManage',@Command;
              COMMIT; END;
            """);
        await context.AdminAsync("GRANT EXECUTE ON Purchasing.CorrectBillUnitForTest TO workbench_web");
        var correction = new JsonObject
        {
            ["schemaVersion"] = 1, ["operation"] = "Reverse", ["expectedConfigurationVersion"] = context.Journal.ConfigurationVersion.ToString(),
            ["purchaseOrderId"] = context.Recognition.PurchaseOrderId.ToString(), ["expectedPurchaseOrderVersion"] = context.Recognition.PurchaseOrderVersion,
            ["unitId"] = draft["revision"]!["units"]![0]!["unitId"]!.DeepClone(), ["postingDate"] = "2026-02-02", ["reason"] = "Attempt generic correction",
            ["expectedEventRevisions"] = new JsonArray(new JsonObject { ["side"] = "Invoice", ["eventRevision"] = 1 }), ["replacement"] = null
        };
        // WHEN generic correction tries to reverse the bill's unit THEN owning source history blocks it.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("CorrectBillUnitForTest", Guid.NewGuid(), correction));
        Assert.Equal(51009, error.Number);
        Assert.Equal(-110m, await context.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(0, await context.Recognition.CountAsync("RecognitionEventCorrections"));
    }
}
