// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditReferenceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DuplicateResolutionMustCoverExactCurrentCreditSet()
    {
        // GIVEN a posted credit with a normalized supplier reference.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var first = await context.CreditCommandAsync(bill, "10");
        first["reference"] = "  Cr  123 ";
        await context.ExecuteRawAsync(first.ToJsonString());
        var second = await context.CreditCommandAsync(bill, "10");
        second["reference"] = "cr 123";
        // WHEN a duplicate is submitted without resolution THEN it conflicts atomically.
        var missing = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(second.ToJsonString()));
        Assert.Equal(51009, missing.Number);
        second["duplicateResolutions"]!.AsArray().Add(new JsonObject
        {
            ["creditId"] = first["creditId"]!.DeepClone(),
            ["creditRevisionId"] = first["creditRevisionId"]!.DeepClone(),
            ["reason"] = "Supplier reused its reference for a separate agreed reduction"
        });
        second["duplicateResolutions"]!.AsArray().Add(second["duplicateResolutions"]![0]!.DeepClone());
        var repeated = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(second.ToJsonString()));
        Assert.Equal(51000, repeated.Number);
        second["duplicateResolutions"]!.AsArray().RemoveAt(1);
        // WHEN the complete current conflict set is explicitly acknowledged THEN it posts.
        await context.ExecuteRawAsync(second.ToJsonString());
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        // WHEN another credit uses an outdated conflict set THEN it must resolve the new member too.
        var stale = await context.CreditCommandAsync(bill, "10");
        stale["reference"] = "CR 123";
        stale["duplicateResolutions"] = second["duplicateResolutions"]!.DeepClone();
        var changed = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(stale.ToJsonString()));
        Assert.Equal(51009, changed.Number);
        Assert.Equal(20m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Total) FROM Purchasing.SupplierCredits"));
    }
}
