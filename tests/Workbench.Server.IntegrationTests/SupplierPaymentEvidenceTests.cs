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
public sealed class SupplierPaymentEvidenceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task EqualDebtAttributionRelinkCannotGrantHistoricalPaymentAuthority()
    {
        // GIVEN two actual equal bills with distinct immutable payable-account versions and a real payment settling both.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var first = await context.Allocation.BillAsync("50"); var payable = context.Bills.Recognition.Accounts["SupplierPayable"];
        var firstVersion = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{payable}'");
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{payable}'");
        await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new JsonObject { ["code"] = code, ["name"] = "Renamed payable", ["description"] = "Current label" }.ToJsonString(), payable, firstVersion);
        var second = await context.Allocation.BillAsync("50"); var payment = await context.CommandAsync();
        await context.AllocateAsync(payment, first, "50"); await context.AllocateAsync(payment, second, "50");
        var result = await context.RecordAsync(payment); var group = result["groupId"]!.GetValue<string>();
        var query = $"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{payment["paymentId"]}')";
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(query));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT Ordinal) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        var firstMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{first}' AND EventKind='Apply'");
        var secondMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{second}' AND EventKind='Apply'");
        var secondAttribution = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}' AND MovementId='{secondMovement}'");
        try
        {
            // WHEN only the second attribution is relinked to the first equal movement, both line totals still agree.
            await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{firstMovement}' WHERE Id='{secondAttribution}'");
            // THEN incomplete per-movement coverage cannot grant historical account authority.
            Assert.Equal(0, await context.Bills.ScalarAsync<int>(query));
        }
        finally
        {
            await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{secondMovement}' WHERE Id='{secondAttribution}'");
        }
        // AND restoring the one changed link restores the original valid proof.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(query));
    }

    [Theory]
    [InlineData("opening")]
    [InlineData("application")]
    [InlineData("funding")]
    [InlineData("snapshot")]
    public async Task GrossControlResolutionRequiresCompleteImmutablePaymentProof(string mutation)
    {
        // GIVEN a fully allocated payment has a provable historical advance despite no advance journal line.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync(); var payment = await context.CommandAsync();
        await context.AllocateAsync(payment, bill, "100"); await context.RecordAsync(payment);
        var id = payment["paymentId"]!.GetValue<string>();
        var query = $"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{id}')";
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(query));
        // WHEN an administrator corrupts one independent proof element in this disposable database.
        var sql = mutation switch
        {
            "opening" => $"DELETE FROM Purchasing.SupplierItemMovements WHERE ItemId='{id}' AND EventKind='Open'",
            "application" => $"UPDATE Purchasing.SupplierApplications SET Amount=99 WHERE FundingItemId='{id}'",
            "funding" => $"UPDATE Accounting.JournalLines SET Credit=99 WHERE AccountId='{context.Bank}'",
            _ => $"UPDATE Purchasing.SupplierPayments SET EvidenceJson=JSON_MODIFY(EvidenceJson,'$.advanceAccount.name','Forged snapshot') WHERE Id='{id}'"
        };
        await context.Bills.AdminAsync(sql);
        // THEN the resolver grants no historical account authority from incomplete or fabricated evidence.
        Assert.Equal(0, await context.Bills.ScalarAsync<int>(query));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("revision")]
    [InlineData("removed")]
    [InlineData("recovery")]
    [InlineData("otherOrder")]
    public async Task EvidenceSnapshotsSurviveChangesButNewPaymentsRequireAvailableOwnedRevision(string mutation)
    {
        // GIVEN an available private PO document proves the corresponding valid command can commit.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var (document, revision) = await context.Bills.SeedDocumentAsync();
        var valid = await context.CommandAsync();
        valid["evidence"] = new JsonObject { ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() }), ["missingEvidenceReason"] = null };
        var request = Guid.NewGuid(); var result = await context.RecordAsync(valid, request);
        var id = valid["paymentId"]!.GetValue<string>();
        var snapshot = await context.Bills.ScalarAsync<string>($"SELECT EvidenceJson FROM Purchasing.SupplierPayments WHERE Id='{id}'");
        Assert.Equal(new string('A', 64), JsonNode.Parse(snapshot)!["evidence"]!["documents"]![0]!["digest"]!.GetValue<string>());
        var bad = await context.CommandAsync(); bad["evidence"] = valid["evidence"]!.DeepClone();
        switch (mutation)
        {
            case "unknown": bad["evidence"]!["documents"]![0]!["documentId"] = Guid.NewGuid().ToString(); break;
            case "revision": bad["evidence"]!["documents"]![0]!["revisionId"] = Guid.NewGuid().ToString(); break;
            case "removed": await context.Bills.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME(),Label='Changed label' WHERE Id='{document}'"); break;
            case "recovery": await context.Bills.AdminAsync($"INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc) VALUES('{JournalTestContext.TenantId}','{revision}',NEWID(),1,'Missing',SYSUTCDATETIME())"); break;
            case "otherOrder":
                await context.Bills.AdminAsync($"""
                    DECLARE @Other uniqueidentifier=NEWID();
                    INSERT Purchasing.DraftOrders(Id,TenantId,IsDeleted,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
                      SELECT @Other,TenantId,0,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId FROM Purchasing.DraftOrders WHERE Id='{context.Bills.Recognition.PurchaseOrderId}';
                    UPDATE Purchasing.PurchaseOrderDocuments SET OrderId=@Other WHERE Id='{document}';
                    """); break;
        }
        var before = await SupplierPaymentValidationTests.Counts(context);
        // WHEN new evidence no longer names an available revision on this PO THEN no source or journal survives.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.RecordAsync(bad))).Number);
        Assert.Equal(before, await SupplierPaymentValidationTests.Counts(context));
        // AND recorded evidence and replay are immutable despite current document changes.
        Assert.Equal(snapshot, await context.Bills.ScalarAsync<string>($"SELECT EvidenceJson FROM Purchasing.SupplierPayments WHERE Id='{id}'"));
        Assert.Equal(result.ToJsonString(), (await context.RecordAsync(valid, request)).ToJsonString());
    }

    [Fact]
    public async Task MissingDocumentEvidenceIsExplicitAndReceiptFaultRollsBackEverything()
    {
        // GIVEN a real successfully recorded missing-document payment and a second payment with one immediate allocation.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var first = await context.CommandAsync(); await context.RecordAsync(first);
        Assert.Equal("Receipt not supplied", await context.Bills.ScalarAsync<string>("SELECT JSON_VALUE(EvidenceJson,'$.evidence.missingEvidenceReason') FROM Purchasing.SupplierPayments"));
        var bill = await context.Allocation.BillAsync(); var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
        var before = await SupplierPaymentValidationTests.Counts(context);
        await context.Bills.AdminAsync("CREATE TRIGGER Purchasing.FailPaymentReceipt ON Purchasing.SupplierFinancialReceipts AFTER INSERT AS THROW 51999,'Payment receipt fault',1;");
        try
        {
            // WHEN the final actual command receipt fails THEN all prior writes in the real command roll back.
            Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() => context.RecordAsync(payment))).Number);
            Assert.Equal(before, await SupplierPaymentValidationTests.Counts(context));
        }
        finally { await context.Bills.AdminAsync("DROP TRIGGER Purchasing.FailPaymentReceipt"); }
        var result = await context.RecordAsync(payment); var group = result["groupId"]!.GetValue<string>();
        // AND a successful retry records one common instant across the complete atomic group.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"""
            SELECT COUNT(DISTINCT RecordedAtUtc) FROM(
              SELECT RecordedAtUtc FROM Purchasing.SupplierFinancialGroups WHERE Id='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierPayments WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierApplications WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierFinancialReceipts WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Accounting.SourceEvents WHERE JSON_VALUE(SnapshotJson,'$.groupId')='{group}'
              UNION ALL SELECT j.RecordedAtUtc FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s ON s.Id=j.SourceEventId WHERE JSON_VALUE(s.SnapshotJson,'$.groupId')='{group}'
              UNION ALL SELECT r.RecordedAtUtc FROM Accounting.PostingReceipts r JOIN Accounting.SourceEvents s ON s.Id=r.SourceEventId WHERE JSON_VALUE(s.SnapshotJson,'$.groupId')='{group}'
            ) instants
            """));
    }

    [Fact]
    public async Task PaymentHistoryLocksSupplierIdentity()
    {
        // GIVEN an ordered PO with a real payment and no bill history.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("UPDATE Purchasing.DraftOrders SET PoNumber=1");
        context.Bills.Recognition.PurchaseOrderVersion = await context.Bills.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.DraftOrders");
        await context.RecordAsync(await context.CommandAsync());
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierId = null, SupplierName = "Replacement supplier", Notes = "Amendment", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        // WHEN an operational amendment tries to detach its supplier THEN immutable payment history retains the guard.
        await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", context.Allocation.Journal.Connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@TargetId", context.Bills.Recognition.PurchaseOrderId); command.Parameters.AddWithValue("@ExpectedVersion", Convert.FromHexString(context.Bills.Recognition.PurchaseOrderVersion[2..]));
        command.Parameters.AddWithValue("@Operation", "Amend"); command.Parameters.AddWithValue("@OrderDate", "2026-01-01"); command.Parameters.AddWithValue("@Reason", "Amendment");
        command.Parameters.AddWithValue("@Draft", JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions)); command.Parameters.AddWithValue("@Calculation", "{}");
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }
}
