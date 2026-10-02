// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemMigrationTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeUsesStoredBillOwnershipWithoutGuessingMissingLinks(bool missingLink)
    {
        // GIVEN a BK-05 bill with authentic posting evidence, optionally missing its owner link.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, "20260928034802_AddSupplierBills");
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        var command = context.PostCommand(reviewed); var request = Guid.NewGuid();
        var posted = await context.ExecuteAsync("PostSupplierBill", request, command);
        if (missingLink) await context.AdminAsync("DELETE Purchasing.SupplierBillPostingEvents");
        var before = await SnapshotAsync(context.Recognition);
        // WHEN upgraded THEN supported ownership is derived; missing ownership remains visibly unresolved.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        Assert.Equal(before, await SnapshotAsync(context.Recognition));
        Assert.Equal(missingLink ? 0 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE BillId IS NOT NULL"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE BillId IS NULL"));
        Assert.Equal(posted.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, command)).ToJsonString());
        Assert.Equal(-110m, await context.Recognition.BalanceAsync("SupplierPayable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHistoricalControlDoesNotPreventSupportedDerivation(bool wrongSourceRevision)
    {
        // GIVEN independent old invoice journals, one with unsupported control-purpose evidence.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer, "20260928034802_AddSupplierBills");
        var valid = await context.CommandAsync("Invoice", cost: "306.60"); await context.PostAsync(valid.ToJsonString());
        var invalid = await context.CommandAsync("Invoice", cost: "40"); var posted = await context.PostAsync(invalid.ToJsonString());
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, wrongSourceRevision
            ? $"UPDATE Accounting.SourceEvents SET SourceRevision=NEWID() WHERE Id=(SELECT SourceEventId FROM Accounting.JournalEntries WHERE Id='{posted.JournalIds[0]}')"
            : $"UPDATE Accounting.JournalLines SET AccountPurpose='General' WHERE JournalId='{posted.JournalIds[0]}' AND AccountPurpose='SupplierPayable'");
        var before = await SnapshotAsync(context);
        // WHEN upgraded THEN only proven control evidence creates capacity and no original evidence changes.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        Assert.Equal(before, await SnapshotAsync(context));
        Assert.Equal(306.60m, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT SUM(Amount) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(1, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.SupplierOpenItems"));
        if (wrongSourceRevision)
        {
            // WHEN derivation is repeated THEN unsupported legacy control remains visible, without guessed capacity.
            var bills = new SupplierBillTestContext(context);
            await SupplierOpenItemRecoveryTests.DeriveAsync(bills);
            var report = await SupplierOpenItemRecoveryTests.ReadAsync(bills);
            Assert.False(report.IsComplete);
            Assert.True(report.UnresolvedTenantControlCount > 0);
            Assert.Equal("306.60", report.Controls.WholeFilterTotals.Payable);
            Assert.Contains(report.Controls.Items, control => control.MissingAttributionCount > 0);
            Assert.Equal(before, await SnapshotAsync(context));
        }
    }

    [Fact]
    public async Task UpgradeDerivesWithoutChangingOriginalBytes()
    {
        // GIVEN legacy invoice/correction evidence on BK-05, including an archived historical AP account.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer, "20260928034802_AddSupplierBills");
        var invoice = await context.CommandAsync("Invoice", cost: "306.60"); var request = Guid.NewGuid();
        var original = await context.PostAsync(invoice.ToJsonString(), request);
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, invoice, "280");
        await context.CorrectAsync(correction.ToJsonString());
        var remapped = await context.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts",
            """[{"code":"2000","name":"Replacement payable","type":"Liability","purpose":"SupplierPayable"}]""");
        var newPayable = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(remapped.Ids)![0];
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, $"""
            UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME() WHERE Id='{context.Accounts["SupplierPayable"]}';
            UPDATE Accounting.Configurations SET Payload=REPLACE(Payload,'{context.Accounts["SupplierPayable"]}','{newPayable}');
            """);
        Assert.Equal(1, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, $"""
            SELECT COUNT(*) FROM Accounting.Configurations CROSS APPLY OPENJSON(Payload,'$.mappings')
            WHERE JSON_VALUE(value,'$.slot')='SupplierPayable' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId'))='{newPayable}'
            """));
        var before = await SnapshotAsync(context);
        // WHEN upgraded THEN only derived evidence is added; original source, journal and receipt bytes survive.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        Assert.Equal(before, await SnapshotAsync(context));
        Assert.Equal(280m, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(0, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, $"SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions WHERE AccountId<>'{context.Accounts["SupplierPayable"]}'"));
        Assert.Equal(306.60m, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements WHERE PostingDate<'2026-03-01'"));
        Assert.Equal(original.EventIds[0], await PurchaseRecognitionCorrectionTests.ScalarAsync<Guid>(context, "SELECT SourceId FROM Purchasing.SupplierFinancialGroups WHERE Operation='OpenRecognitionPayable'"));
        await context.PostAsync(invoice.ToJsonString(), request);
        Assert.Equal(before, await SnapshotAsync(context));
    }

    internal static Task<string> SnapshotAsync(PurchaseRecognitionTestContext context) => new SupplierBillTestContext(context).ScalarAsync<string>("""
        SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
          (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH),
          (SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Accounting.PostingReceipts ORDER BY TenantId,RequestId FOR JSON PATH),
          (SELECT * FROM Accounting.CorrectionGroups ORDER BY TenantId,Id FOR JSON PATH),
          (SELECT * FROM Accounting.CorrectionReceipts ORDER BY TenantId,RequestId FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBills ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillRevisions ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillReviews ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillEvidence ORDER BY TenantId,ReviewId,DocumentId FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillPostings ORDER BY TenantId,BillId FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillPostingEvents ORDER BY TenantId,BillId,EventId FOR JSON PATH),
          (SELECT * FROM Purchasing.SupplierBillReceipts ORDER BY TenantId,Sequence FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionUnits ORDER BY TenantId,Id FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionComponents ORDER BY TenantId,EventId,ComponentKey FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionMatches ORDER BY TenantId,Id FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionCorrectionGroups ORDER BY TenantId,Id FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionSideEvents ORDER BY Id FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionEventCorrections ORDER BY OriginalEventId FOR JSON PATH),
          (SELECT * FROM Purchasing.RecognitionGroupReceipts ORDER BY RequestId FOR JSON PATH))),2)
        """);
}
