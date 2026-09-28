// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PaymentPreservesDistinctHistoricalVersionsOfTheSamePayableAccount()
    {
        // GIVEN two bills posted on either side of a real payable-account rename.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var first = await context.Allocation.BillAsync("60"); var payable = context.Bills.Recognition.Accounts["SupplierPayable"];
        var firstVersion = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{payable}'");
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{payable}'");
        var renamed = await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new System.Text.Json.Nodes.JsonObject { ["code"] = code, ["name"] = "Renamed payable", ["description"] = "Current label" }.ToJsonString(), payable, firstVersion);
        var second = await context.Allocation.BillAsync("40"); var payment = await context.CommandAsync();
        await context.AllocateAsync(payment, first, "60"); await context.AllocateAsync(payment, second, "40");
        // WHEN one payment settles both bills THEN each debit line retains its source's precise historical snapshot.
        var result = await context.RecordAsync(payment); var journal = result["journalIds"]![0]!.GetValue<string>();
        Assert.Equal(0m, await context.Allocation.BalanceAsync(first)); Assert.Equal(0m, await context.Allocation.BalanceAsync(second));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{payable}' AND AccountVersion='{firstVersion}' AND AccountName='SupplierPayable' AND Debit=60"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{payable}' AND AccountVersion='{renamed.Version}' AND AccountName='Renamed payable' AND Debit=40"));
        // AND there is still just one actual funding credit and a provable gross advance behind the zero net balance.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{context.Bank}' AND Credit=100"));
        Assert.Equal(3, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{payment["paymentId"]}')"));
    }

    [Fact]
    public async Task MixedPaymentPreservesConfiguredFourthDecimal()
    {
        // GIVEN configured scale four and a bill of 100 with a payment one smallest unit larger.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.policies.scale',4)");
        var bill = await context.Allocation.BillAsync("100");
        var payment = await context.CommandAsync("100.0001"); await context.AllocateAsync(payment, bill, "100");
        // WHEN the wide application total is subtracted THEN the smallest configured unit remains exact.
        await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.GetValue<string>());
        Assert.Equal(0.0001m, await context.Allocation.BalanceAsync(id));
        Assert.Equal(0.0001m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Advance}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{id}')"));
    }

    [Fact]
    public async Task DepositBillAndFinalPaymentReconcile()
    {
        // GIVEN an actual deposit before the bill is recorded.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var deposit = await context.CommandAsync("100", "2026-09-10");
        await context.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        Assert.Equal(100m, await context.Allocation.BalanceAsync(advance));
        var bill = await context.Allocation.BillAsync("306.60");
        // WHEN the deposit is applied and the remaining amount is paid directly against the bill.
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(advance, bill));
        Assert.Equal(206.60m, await context.Allocation.BalanceAsync(bill));
        var final = await context.CommandAsync("206.60"); await context.AllocateAsync(final, bill, "206.60");
        await context.RecordAsync(final);
        // THEN both liabilities and advances clear and there is exactly one funding credit per payment.
        Assert.Equal(0m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(0m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(m.Amount) FROM Purchasing.SupplierItemMovements m JOIN Purchasing.SupplierOpenItems i ON i.Id=m.ItemId WHERE i.Kind='Advance'"));
        Assert.Equal(-306.60m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}' AND Credit>0"));
    }

    [Theory]
    [InlineData("100", 0)]
    [InlineData("140", 40)]
    public async Task FullyAllocatedPaymentRetainsGrossCapacityAndMixedPaymentRetainsRemainder(string amount, decimal remainder)
    {
        // GIVEN two bills sharing a historical payable account that has since been archived.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var first = await context.Allocation.BillAsync("60"); var second = await context.Allocation.BillAsync("40");
        var laterBill = remainder > 0 ? await context.Allocation.BillAsync("40") : Guid.Empty;
        var payment = await context.CommandAsync(amount);
        await context.AllocateAsync(payment, first, "60"); await context.AllocateAsync(payment, second, "40");
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID() WHERE Purpose='SupplierPayable'");
        // WHEN the real payment is recorded and replayed.
        var request = Guid.NewGuid(); var result = await context.RecordAsync(payment, request);
        Assert.Equal(result.ToJsonString(), (await context.RecordAsync(payment, request)).ToJsonString());
        var id = Guid.Parse(payment["paymentId"]!.GetValue<string>());
        // THEN gross source capacity persists, applications consume 100, and the journal only has nonzero net lines.
        Assert.Equal(decimal.Parse(amount), await context.Bills.ScalarAsync<decimal>($"SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements WHERE ItemId='{id}' AND EventKind='Open'"));
        Assert.Equal(remainder, await context.Allocation.BalanceAsync(id));
        Assert.Equal(0m, await context.Allocation.BalanceAsync(first)); Assert.Equal(0m, await context.Allocation.BalanceAsync(second));
        Assert.Equal(2, result["applicationIds"]!.AsArray().Count); Assert.Single(result["journalIds"]!.AsArray());
        Assert.NotNull(result["version"]); Assert.Equal(id.ToString(), result["advanceItemId"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(remainder == 0 ? 0 : 1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE AccountId='{context.Advance}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{id}')"));
        if (remainder > 0)
        {
            // AND later application uses the authentic original advance account after it is archived and edited.
            await context.Bills.AdminAsync($"UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID(),Name='Archived advance' WHERE Id='{context.Advance}'");
            await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, laterBill, "40"));
            Assert.Equal(0m, await context.Allocation.BalanceAsync(id)); Assert.Equal(0m, await context.Allocation.BalanceAsync(laterBill));
            Assert.Equal(0m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Advance}'"));
        }
    }
}
