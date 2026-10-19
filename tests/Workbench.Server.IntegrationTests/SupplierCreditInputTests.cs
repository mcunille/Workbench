// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditInputTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("18.001")]
    [InlineData("18.00001")]
    [InlineData("1000000000000000000000000")]
    public async Task InvalidAmountsLeaveNoCreditOrJournal(string amount)
    {
        // GIVEN a recognized bill and an amount outside positive exact USD storage policy.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var before = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries");
        var command = await context.CreditCommandAsync(bill, amount);
        // WHEN the source command rejects it THEN neither source nor journal was partially recorded.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(command.ToJsonString()));
        Assert.Equal(51000, error.Number);
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(before, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries"));
    }

    [Theory]
    [InlineData("long-reason")]
    [InlineData("trailing-key")]
    [InlineData("duplicate-property")]
    [InlineData("account-override")]
    public async Task NoncanonicalFieldsRejectBeforeSourceMutation(string shape)
    {
        // GIVEN an otherwise valid source with a malformed or unsupported property.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var command = await context.CreditCommandAsync(bill, "18");
        if (shape == "long-reason") command["reason"] = new string('r', 4001);
        if (shape == "trailing-key")
        {
            command["currency "] = command["currency"]!.DeepClone();
            command.Remove("currency");
        }
        if (shape == "account-override") command["accountId"] = Guid.NewGuid().ToString();
        if (shape == "duplicate-property") command.Remove("reason");
        var json = command.ToJsonString();
        if (shape == "duplicate-property") json = json[..^1] + ",\"reference\":\"duplicate\"}";

        // WHEN submitted THEN strict input validation, rather than a downstream SQL constraint, rejects it.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(json));
        Assert.Equal(51000, error.Number);
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }
}
