// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditCorrectionClosureTests(SupplierCreditClosureFixture history) : IClassFixture<SupplierCreditClosureFixture>
{
    [Fact]
    public async Task OversizedClosureRejectsWithoutTruncatingRealRefundHistory()
    {
        // GIVEN over one thousand dependency/effect events from genuine refund commands in an isolated clone.
        await using var context = await history.OpenAsync();
        var command = await context.CorrectionCommandAsync("Credit", history.CreditId, null);
        // WHEN the complete correction is previewed THEN its bound fails before partial acceptance.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        Assert.Equal(334, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
        Assert.Equal(3.34m, await context.BankBalanceAsync());
        Assert.Equal(16.66m, await context.BalanceAsync(history.CreditId));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
    }
}
