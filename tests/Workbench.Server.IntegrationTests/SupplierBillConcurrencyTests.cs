// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentPostsHaveOneDurableFinancialResult(bool sameRequest)
    {
        // GIVEN a reviewed bill and independent restricted SQL connections.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context); var input = context.PostCommand(reviewed); var request = Guid.NewGuid();
        await using var sibling = await context.Journal.OpenSiblingAsync();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(context.Journal.Application.AdminConnectionString);
        // WHEN both source transactions contend for the common financial lock.
        var first = context.ExecuteAsync("PostSupplierBill", request, input);
        var second = context.ExecuteAsync("PostSupplierBill", sameRequest ? request : Guid.NewGuid(), input, sibling);
        await gate.WaitForBlockedAsync(context.Journal.Connection, sibling);
        await gate.ReleaseAsync();
        var results = await Task.WhenAll(CaptureAsync(first), CaptureAsync(second));
        // THEN the durable source is posted once; only the exact retry shares its receipt.
        if (sameRequest)
        {
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(results[0].Result!.ToJsonString(), results[1].Result!.ToJsonString());
        }
        else
        {
            Assert.Single(results, r => r.Error is null);
            Assert.Equal(51009, Assert.Single(results, r => r.Error is not null).Error!.Number);
        }
        Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(-110m, await context.Recognition.BalanceAsync("SupplierPayable"));
    }

    [Fact]
    public async Task PostRetryRechecksBothPermissionsBeforeReturningReceipt()
    {
        // GIVEN a posted invoice and its exact successful request.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context); var input = context.PostCommand(reviewed); var request = Guid.NewGuid();
        var posted = await context.ExecuteAsync("PostSupplierBill", request, input);
        // WHEN mutable purchase state changes THEN exact retry still returns its original receipt.
        await context.AdminAsync("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=SYSUTCDATETIME()");
        Assert.Equal(posted.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, input)).ToJsonString());
        // AND losing either business permission denies that same replay without changing the posting.
        await context.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue=N'SupplierBillsPost'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", request, input))).Number);
        Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
    }

    private static async Task<(JsonObject? Result, SqlException? Error)> CaptureAsync(Task<JsonObject> task)
    { try { return (await task, null); } catch (SqlException error) { return (null, error); } }
}
