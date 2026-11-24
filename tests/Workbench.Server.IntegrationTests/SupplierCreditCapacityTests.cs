// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditCapacityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task FourDecimalCapacityDoesNotRoundAwaySmallClaims()
    {
        // GIVEN a four-decimal currency and a fully consumed original cost of 1.0001.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.policies.scale',4)");
        var bill = await context.ExpenseBillAsync("1.0001");
        await context.CreditAsync(bill, "1");
        await context.CreditAsync(bill, "0.0001");
        // WHEN another smallest-unit claim is submitted THEN exact capacity rejects it.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CreditAsync(bill, "0.0001"));
        Assert.Equal(51009, error.Number);
        Assert.Equal(1.0001m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Total) FROM Purchasing.SupplierCredits"));
    }

    [Theory]
    [InlineData("Inventory", "0", "base")]
    [InlineData("Expense", "10", "tax")]
    public async Task UnsupportedPositionsCannotBeInferredFromAccountBalance(string classification, string tax, string component)
    {
        // GIVEN authentic inventory or recoverable tax with no remaining-position source owner.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100", classification: classification, tax: tax);
        var command = await context.CreditCommandAsync(bill, "5");
        command["claims"]![0]!["componentKey"] = component;
        // WHEN a reduction is requested THEN a positive ledger balance provides no authority.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(command.ToJsonString()));
        Assert.Equal(51004, error.Number);
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

    [Fact]
    public async Task OriginalAssignedRoundingCanBeCreditedOnlyOnce()
    {
        // GIVEN the original approved minor-unit rounding assigned to the base cost.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100", rounding: "0.01", freight: "5");
        // WHEN the entire supported cost is credited, including that original attribution.
        await context.CreditAsync(bill, "100.01");
        // THEN a separate rounding claim cannot create another allowance.
        var extra = await context.CreditCommandAsync(bill, "0.01");
        extra["claims"]![0]!["componentKey"] = "rounding";
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(extra.ToJsonString()));
        Assert.Equal(51009, error.Number);
        Assert.Equal(5m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Fact]
    public async Task CreditPreservesOriginalExpenseAccountSnapshotAfterRename()
    {
        // GIVEN an expense bill whose original expense account has since been renamed.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var expense = context.Bills.Recognition.Accounts["Expense"];
        var version = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{expense}'");
        var name = await context.Bills.ScalarAsync<string>($"SELECT Name FROM Accounting.Accounts WHERE Id='{expense}'");
        var replacement = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts",
            """[{"code":"NEWEXP","name":"Replacement expense","type":"Expense","purpose":"General"}]""");
        var replacementId = Guid.Parse(JsonNode.Parse(replacement.Ids)![0]!.GetValue<string>());
        await context.Bills.AdminAsync($$"""
            DECLARE @path nvarchar(100)=(SELECT '$.mappings['+[key]+'].accountId' FROM Accounting.Configurations c
              CROSS APPLY OPENJSON(c.Payload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='Expense');
            UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,@path,N'{{replacementId}}');
            """);
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{expense}'");
        await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new JsonObject { ["code"] = code, ["name"] = "Renamed expense", ["description"] = "Updated name after posting" }.ToJsonString(), expense, version);
        // WHEN a credit reduces the authentic historical cost.
        var result = await context.CreditAsync(bill, "20");
        var journal = Guid.Parse(result["journalIds"]![0]!.GetValue<string>());
        // THEN the offset retains its original account revision and name.
        Assert.Equal(version, await context.Bills.ScalarAsync<Guid>($"SELECT AccountVersion FROM Accounting.JournalLines WHERE JournalId='{journal}' AND Credit=20"));
        Assert.Equal(name, await context.Bills.ScalarAsync<string>($"SELECT AccountName FROM Accounting.JournalLines WHERE JournalId='{journal}' AND Credit=20"));
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Fact]
    public async Task CreditAllocationIsExplicitAndKeepsGrossCapacity()
    {
        // GIVEN a recognized bill and an explicit partial application of its credit.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var command = await context.CreditCommandAsync(bill, "30");
        command["allocations"]!.AsArray().Add(new JsonObject
        {
            ["billId"] = bill.ToString(),
            ["itemId"] = bill.ToString(),
            ["expectedItemVersion"] = await context.Payments.Allocation.VersionAsync(bill),
            ["amount"] = "20"
        });
        // WHEN the credit and application commit through their source owner.
        var result = await context.ExecuteRawAsync(command.ToJsonString());
        var credit = Guid.Parse(command["creditId"]!.GetValue<string>());
        var group = Guid.Parse(result["groupId"]!.GetValue<string>());
        // THEN gross credit remains thirty, with twenty applied and no cash movement.
        Assert.Equal(80m, await context.BalanceAsync(bill));
        Assert.Equal(10m, await context.BalanceAsync(credit));
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(30m, await context.Bills.ScalarAsync<decimal>($"SELECT Total FROM Purchasing.SupplierCredits WHERE Id='{credit}'"));
        Assert.Equal(3, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT RecordedAtUtc) FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}'"));
        Assert.Equal(2, result["journalIds"]!.AsArray().Count);
        Assert.Single(result["applicationIds"]!.AsArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentClaimsCannotExceedOriginalComponent(bool firstWins)
    {
        // GIVEN independent connections claiming sixty each from one hundred of expense.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var first = await context.CreditCommandAsync(bill, "60");
        var second = await context.CreditCommandAsync(bill, "60");
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        Task PostFirst() => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), first, context.Bills.Journal.Connection);
        Task PostSecond() => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), second, sibling);
        // WHEN both serial orders are forced with the contender demonstrably blocked in SQL.
        var error = await PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            firstWins ? context.Bills.Journal.Connection : sibling, firstWins ? sibling : context.Bills.Journal.Connection,
            firstWins ? PostFirst : PostSecond, firstWins ? PostSecond : PostFirst);
        // THEN only one source posts and the loser leaves no partial credit.
        Assert.Equal(51009, error?.Number);
        Assert.Equal(firstWins ? first["creditId"]!.GetValue<string>() : second["creditId"]!.GetValue<string>(),
            (await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Purchasing.SupplierCredits")).ToString());
        Assert.Equal(60m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Total) FROM Purchasing.SupplierCredits"));
    }
}
