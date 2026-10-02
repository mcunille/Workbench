// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentValidationTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(true)]
    public async Task ActualPastCashPaymentCanUseAnOpenFuturePostingDate(bool futureEffective)
    {
        // GIVEN an actual cash payment already made, with a future open posting date.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var cash = await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """[{"code":"CASH4","name":"Cash","type":"Asset","purpose":"Cash"}]""");
        var cashId = JsonNode.Parse(cash.Ids)![0]!.GetValue<string>();
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd");
        var payment = await context.CommandAsync("100", future);
        payment["paymentDate"] = "2026-09-16"; payment["effectiveDate"] = futureEffective ? future : "2026-09-16";
        payment["method"] = "Cash"; payment["fundingAccountId"] = cashId;
        payment["expectedFundingAccountVersion"] = (await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{cashId}'")).ToString();
        // WHEN recording under the kernel's open-period policy THEN future posting itself is allowed.
        await context.RecordAsync(payment);
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{cashId}'"));
        Assert.Equal("Cash", await context.Bills.ScalarAsync<string>("SELECT FundingAccountPurpose FROM Purchasing.SupplierPayments"));
    }

    [Fact]
    public async Task ImmediateAllocationRequiresCurrentAllocationAuthorityBeforeReplay()
    {
        // GIVEN a successfully recorded payment with an allocation and its actual receipt.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync(); var payment = await context.CommandAsync();
        await context.AllocateAsync(payment, bill, "100"); var request = Guid.NewGuid();
        await context.RecordAsync(payment, request);
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='SupplierAllocationsManage'");
        // WHEN allocation authority is revoked THEN replay is denied, while Record alone still permits a deposit.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.RecordAsync(payment, request))).Number);
        await context.RecordAsync(await context.CommandAsync());
    }

    [Theory]
    [InlineData("zero,negative,scale,overflow,future,effective,method", 51000)]
    [InlineData("unknownField,longNotes,longReference,longMethod", 51000)]
    [InlineData("general", 51004)]
    [InlineData("archived", 51004)]
    [InlineData("staleFunding,overPayment,overBill", 51009)]
    [InlineData("advanceArchived", 51004)]
    public async Task IndependentPaymentGuardsRejectWithoutPartialWrites(string guards, int number)
    {
        // GIVEN a real valid payment succeeds before changing one independent guard.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("500");
        var valid = await context.CommandAsync(); await context.AllocateAsync(valid, bill, "100");
        await context.RecordAsync(valid);
        foreach (var guard in guards.Split(','))
        {
            var bad = await context.CommandAsync(); await context.AllocateAsync(bad, bill, "100");
            switch (guard)
            {
                case "zero": bad["amount"] = "0"; break;
                case "negative": bad["amount"] = "-1"; break;
                case "scale": bad["amount"] = "100.001"; break;
                case "overflow": bad["amount"] = "1000000000000000000000000"; break;
                case "future": bad["paymentDate"] = "2099-01-01"; bad["postingDate"] = "2099-01-01"; bad["effectiveDate"] = "2099-01-01"; break;
                case "effective": bad["effectiveDate"] = "2026-09-17"; break;
                case "method": bad["method"] = "Refund"; break;
                case "general":
                    var general = context.Bills.Recognition.Accounts["Prepayment"];
                    bad["fundingAccountId"] = general.ToString();
                    bad["expectedFundingAccountVersion"] = (await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{general}'")).ToString();
                    break;
                case "archived": await context.Bills.AdminAsync($"UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME() WHERE Id='{context.Bank}'"); break;
                case "staleFunding": bad["expectedFundingAccountVersion"] = Guid.NewGuid().ToString(); break;
                case "overPayment": bad["allocations"]![0]!["amount"] = "101"; break;
                case "overBill": bad["amount"] = "401"; bad["allocations"]![0]!["amount"] = "401"; break;
                case "unknownField": bad["bankFee"] = "1"; break;
                case "longNotes": bad["notes"] = new string('x', 5000); break;
                case "longReference": bad["reference"] = new string('x', 5000); break;
                case "longMethod": bad["method"] = new string('x', 5000); break;
                case "advanceArchived": await context.Bills.AdminAsync($"UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME() WHERE Id='{context.Advance}'"); break;
            }
            var before = await Counts(context);
            // WHEN the changed request reaches the production command THEN the intended guard rejects atomically.
            var error = await Record.ExceptionAsync(() => context.RecordAsync(bad));
            Assert.True(error is SqlException, $"{guard}: expected SQL rejection, got {error?.GetType().Name ?? "success"}.");
            Assert.True(((SqlException)error!).Number == number, $"{guard}: {error.Message}");
            Assert.True(before == await Counts(context), $"{guard}: financial evidence changed.");
            Assert.True(400m == await context.Allocation.BalanceAsync(bill), $"{guard}: bill capacity changed.");
        }
    }

    [Fact]
    public async Task RawJsonRejectsBeforePersistence()
    {
        // GIVEN the exact complete command shape has already succeeded.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.RecordAsync(await context.CommandAsync());
        var validRaw = (await context.CommandAsync()).ToJsonString();
        foreach (var kind in new[] { "malformed", "duplicate" })
        {
            var raw = validRaw;
            raw = kind == "malformed" ? raw[..^1] : raw.Insert(1, "\"amount\":\"100\",");
            var before = await Counts(context);
            // WHEN malformed or duplicate JSON is sent without a client parser normalizing it THEN nothing persists.
            await using var command = new SqlCommand("EXEC Purchasing.RecordSupplierPayment @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@json", context.Allocation.Journal.Connection);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
            command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@json", raw);
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            Assert.Equal(before, await Counts(context));
        }
    }

    internal static Task<string> Counts(SupplierPaymentTestContext context) => context.Bills.ScalarAsync<string>("""
        SELECT CONCAT((SELECT COUNT(*) FROM Accounting.SourceEvents),':',(SELECT COUNT(*) FROM Accounting.JournalEntries),':',
          (SELECT COUNT(*) FROM Accounting.JournalLines),':',(SELECT COUNT(*) FROM Accounting.PostingReceipts),':',
          (SELECT COUNT(*) FROM Purchasing.SupplierPayments),':',(SELECT COUNT(*) FROM Purchasing.SupplierOpenItems),':',
          (SELECT COUNT(*) FROM Purchasing.SupplierApplications),':',(SELECT COUNT(*) FROM Purchasing.SupplierItemMovements),':',
          (SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions),':',(SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups),':',
          (SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts))
        """);
}
