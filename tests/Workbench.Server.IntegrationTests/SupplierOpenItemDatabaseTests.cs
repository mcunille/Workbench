// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CannotCommitUnbalancedOrUnattributedGroup()
    {
        // GIVEN a real posted invoice-side payable and a restricted principal's fixture adapter.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var valid = await context.PostedInvoiceOpeningAsync();
        // WHEN an opening group exactly covers the posted payable control line.
        await context.ExecuteAsync("AppendOpening", valid);
        // THEN the independent journal-control amount and attributed movement agree.
        var acceptedAccountDifference = await context.ScalarAsync<decimal>("""
            SELECT COALESCE((SELECT SUM(l.Credit-l.Debit) FROM Accounting.JournalLines l
              WHERE l.AccountPurpose='SupplierPayable'),0)
              - COALESCE((SELECT SUM(Amount) FROM Purchasing.SupplierControlAttributions),0)
            """);
        Assert.Equal(0m, acceptedAccountDifference);

        foreach (var mismatch in new[] { "WrongAmount", "MissingControlLine" })
        {
            // GIVEN a new posted invoice with a false amount or an absent journal line.
            var invalid = await context.PostedInvoiceOpeningAsync(amount: mismatch == "WrongAmount" ? "109.00" : "110.00");
            if (mismatch == "MissingControlLine") invalid["events"]![0]!["ordinal"] = 999;
            var beforeRows = await context.ScalarAsync<int>("""
                SELECT (SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierOpenItems)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierItemMovements)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions)
                """);
            // WHEN the group has no exact control coverage THEN no participant evidence commits.
            await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("AppendOpening", invalid));
            var afterRows = await context.ScalarAsync<int>("""
                SELECT (SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierOpenItems)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierItemMovements)
                  +(SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions)
                """);
            Assert.Equal(beforeRows, afterRows);
        }
    }

    [Fact]
    public async Task RuntimeCannotMutateSupplierEvidence()
    {
        // GIVEN an accepted source-owned payable group and the actual web principal.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var opening = await context.PostedInvoiceOpeningAsync();
        await context.ExecuteAsync("AppendOpening", opening);
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierItemMovements"));
        // WHEN the runtime attempts direct financial DML THEN the row remains immutable.
        var runtimeDirectWriteSucceeded = false;
        try
        {
            await using var command = new SqlCommand("UPDATE Purchasing.SupplierItemMovements SET Amount=111; SELECT @@ROWCOUNT", context.Journal.Connection);
            runtimeDirectWriteSucceeded = (int)(await command.ExecuteScalarAsync())! > 0;
        }
        catch (SqlException error) when (error.Number == 229) { }
        Assert.False(runtimeDirectWriteSucceeded);
        // AND the participant itself is not an executable runtime capability.
        await using var direct = new SqlCommand("EXEC Purchasing.AppendSupplierEventGroup @tenant,@group,@recorded,@events", context.Journal.Connection);
        direct.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        direct.Parameters.AddWithValue("@group", Guid.NewGuid());
        direct.Parameters.AddWithValue("@recorded", DateTimeOffset.UtcNow);
        direct.Parameters.AddWithValue("@events", "[]");
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => direct.ExecuteNonQueryAsync())).Number);
    }

    [Fact]
    public async Task FinancialCommandCanonicalizationRejectsUnknownNestedClaims()
    {
        // GIVEN a structurally valid allocation request with the same values in different property order.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var target = new System.Text.Json.Nodes.JsonObject
        {
            ["itemId"] = Guid.NewGuid().ToString(),
            ["expectedItemVersion"] = "0x0000000000000001",
            ["amount"] = "1.00"
        };
        var first = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = "ApplySupplierFunds",
            ["targets"] = new System.Text.Json.Nodes.JsonArray(target.DeepClone())
        };
        var reordered = new System.Text.Json.Nodes.JsonObject
        {
            ["targets"] = first["targets"]!.DeepClone(),
            ["operation"] = "ApplySupplierFunds",
            ["schemaVersion"] = 1
        };
        // WHEN both envelopes are normalized THEN property order does not change replay identity.
        Assert.Equal(await context.ValidateAsync("ApplySupplierFunds", first),
            await context.ValidateAsync("ApplySupplierFunds", reordered));
        // WHEN a target carries an unknown authority claim THEN structural validation rejects it.
        first["targets"]![0]!["authority"] = true;
        await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", first));
    }

    [Fact]
    public async Task FinancialCommandValidatorRejectsDuplicateAndOversizedTargetSets()
    {
        // GIVEN two allocations naming the same item in an otherwise valid envelope.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var target = new System.Text.Json.Nodes.JsonObject
        {
            ["billId"] = Guid.NewGuid().ToString(),
            ["itemId"] = Guid.NewGuid().ToString(),
            ["expectedItemVersion"] = "0x0000000000000001",
            ["amount"] = "1.00"
        };
        var command = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = "ApplySupplierFunds",
            ["targets"] = new System.Text.Json.Nodes.JsonArray(target.DeepClone(), target.DeepClone())
        };
        // WHEN a duplicate target is supplied THEN it cannot canonicalize.
        await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", command));
        var targets = command["targets"]!.AsArray();
        targets.Clear();
        for (var i = 0; i < 1001; i++)
        {
            var distinct = target.DeepClone();
            distinct["itemId"] = Guid.NewGuid().ToString();
            targets.Add(distinct);
        }
        // WHEN this target set also exceeds the envelope byte cap THEN it cannot canonicalize.
        // The target-count boundary needs a separate compact-command test.
        await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", command));
    }

    [Fact]
    public async Task FinancialCommandValidatorDeduplicatesParsedItemIdentity()
    {
        // GIVEN two allocation targets with distinct item identities in an otherwise valid command.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var firstItem = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var secondItem = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var command = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = "ApplySupplierFunds",
            ["targets"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["itemId"] = firstItem, ["amount"] = "1.00" },
                new System.Text.Json.Nodes.JsonObject { ["itemId"] = secondItem, ["amount"] = "1.00" })
        };
        // WHEN the validator sees distinct parsed GUIDs THEN it accepts the command.
        Assert.False(string.IsNullOrEmpty(await context.ValidateAsync("ApplySupplierFunds", command)));

        // WHEN case changes the second text to the first GUID THEN the duplicate identity is rejected.
        command["targets"]![1]!["itemId"] = firstItem.ToUpperInvariant();
        await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", command));
    }
}
