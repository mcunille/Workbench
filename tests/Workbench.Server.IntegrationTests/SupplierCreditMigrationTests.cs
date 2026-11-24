// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditMigrationTests(SupplierCreditLegacyFixture legacy) : IClassFixture<SupplierCreditLegacyFixture>
{
    [Fact]
    public async Task UpgradePreservesExistingFinancialBytesAndExactReplay()
    {
        // GIVEN a restored database of the actual predecessor migration, with a posted bill and held payment evidence.
        await using var context = await legacy.OpenAsync();
        var bills = context.Bills;
        Assert.Equal(SupplierCreditLegacyFixture.PriorMigration,
            await bills.ScalarAsync<string>("SELECT MAX(MigrationId) FROM dbo.__EFMigrationsHistory"));
        Assert.True(await bills.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        var before = await RetainedBytes(bills);
        var bill = JsonNode.Parse(await bills.ScalarAsync<string>($"""
            SELECT RequestId requestId,JSON_QUERY(CanonicalInput) command,JSON_QUERY(ResultJson) receipt
            FROM Purchasing.SupplierBillReceipts WHERE BillId='{legacy.BillId}' AND Operation='Post'
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
            """))!.AsObject();

        // WHEN the single release migration is installed and both authorized predecessor commands replay.
        await DatabaseMigrator.MigrateAsync(bills.Journal.Application.AdminConnectionString, default);
        await MigrationHistoryAssertions.AssertCurrentAsync(bills.Journal.Application.AdminConnectionString);
        Assert.Equal(SupplierCreditLegacyFixture.ReleaseMigration,
            await bills.ScalarAsync<string>("SELECT MAX(MigrationId) FROM dbo.__EFMigrationsHistory"));
        var paymentReplay = await legacy.ReplayPaymentAsync(context);
        var billReplay = await bills.ExecuteAsync("PostSupplierBill", Guid.Parse(bill["requestId"]!.ToString()), bill["command"]!.AsObject());

        // THEN receipts replay byte-for-byte at the response boundary and retained rows are identical.
        Assert.Equal(legacy.PaymentReceipt, paymentReplay.ToJsonString());
        Assert.Equal(bill["receipt"]!.ToJsonString(), billReplay.ToJsonString());
        Assert.Equal(before, await RetainedBytes(bills));
        Assert.True(await bills.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        Assert.True((await SupplierReconciliationTests.Read(context)).IsComplete);
    }

    private static Task<string> RetainedBytes(SupplierBillTestContext bills) => bills.ScalarAsync<string>("""
        SELECT (SELECT
            JSON_QUERY((SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH)) sources,
            JSON_QUERY((SELECT * FROM Accounting.JournalEntries ORDER BY Sequence FOR JSON PATH)) journals,
            JSON_QUERY((SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH)) lines,
            JSON_QUERY((SELECT * FROM Accounting.PostingReceipts ORDER BY RequestId FOR JSON PATH)) journalReceipts,
            JSON_QUERY((SELECT * FROM Purchasing.SupplierBillReceipts ORDER BY RequestId FOR JSON PATH)) billReceipts,
            JSON_QUERY((SELECT * FROM Purchasing.SupplierFinancialReceipts ORDER BY RequestId FOR JSON PATH)) supplierReceipts,
            JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceSets ORDER BY Id FOR JSON PATH)) evidence,
            JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceLinks ORDER BY Id FOR JSON PATH)) links,
            JSON_QUERY((SELECT Id,Held,IndependentHeld FROM Storage.Attachments ORDER BY Id FOR JSON PATH)) holds
          FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
        """);
}
