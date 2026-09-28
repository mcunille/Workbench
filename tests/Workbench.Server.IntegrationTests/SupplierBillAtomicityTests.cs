// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillAtomicityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task FailedJournalWriteRollsBackBillAndRecognitionThenRetryCommitsOnce()
    {
        // GIVEN a reviewed bill and a disposable database fault on journal persistence.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context); var input = context.PostCommand(reviewed); var request = Guid.NewGuid();
        await context.AdminAsync("CREATE TRIGGER Accounting.SupplierBillTestFault ON Accounting.JournalEntries AFTER INSERT AS THROW 51055, 'Disposable bill posting fault.', 1;");
        // WHEN the inner kernel fails THEN the outer source transaction has no partial effects or receipt.
        Assert.Equal(51055, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", request, input))).Number);
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.Recognition.CountAsync("RecognitionSideEvents"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillReceipts WHERE Operation='Post'"));
        Assert.Equal("Reviewed", await context.ScalarAsync<string>("SELECT State FROM Purchasing.SupplierBills"));
        // AND removing the fixture fault permits the identical request to commit once.
        await context.AdminAsync("DROP TRIGGER Accounting.SupplierBillTestFault");
        Assert.Equal("Posted", (await context.ExecuteAsync("PostSupplierBill", request, input))["state"]!.GetValue<string>());
        Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
    }

    [Fact]
    public async Task ExpandedRecognitionEnvelopeRejectsAtomically()
    {
        // GIVEN bounded stored source units whose derived identity/evidence envelope is larger than the kernel limit.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft(); var prototype = draft["revision"]!["units"]![0]!.DeepClone();
        var units = new JsonArray();
        for (var i = 0; i < 180; i++)
        {
            var unit = prototype.DeepClone(); unit["unitId"] = Guid.NewGuid().ToString(); unit["componentKey"] = $"line-{i}";
            units.Add(unit);
        }
        draft["revision"]!["units"] = units; draft["revision"]!["total"] = "19800.00";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        // WHEN expansion exceeds 262,144 bytes THEN the source remains reviewed with no journal.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed)))).Number);
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal("Reviewed", await context.ScalarAsync<string>("SELECT State FROM Purchasing.SupplierBills"));
    }
}
