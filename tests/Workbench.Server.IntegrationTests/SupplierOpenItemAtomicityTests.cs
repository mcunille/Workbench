// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemAtomicityTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false, "Accounting.SourceEvents")]
    [InlineData(false, "Accounting.JournalEntries")]
    [InlineData(false, "Purchasing.SupplierControlAttributions")]
    [InlineData(true, "Accounting.SourceEvents")]
    [InlineData(true, "Accounting.JournalEntries")]
    [InlineData(true, "Purchasing.SupplierControlAttributions")]
    public async Task FailedPostingLeavesNoFinancialEvidence(bool application, string faultTable)
    {
        // GIVEN a real bill and payment source; receipt-stage faults have separate existing owners.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync();
        var deposit = await context.CommandAsync();
        await context.RecordAsync(deposit);
        var command = application
            ? await context.Allocation.CommandAsync(Guid.Parse(deposit["paymentId"]!.ToString()), bill)
            : await context.CommandAsync();
        if (!application) await context.AllocateAsync(command, bill, "100");
        var operation = application ? "ApplySupplierFunds" : "RecordSupplierPayment";
        var request = Guid.NewGuid();
        var before = await SnapshotAsync(context);
        var schema = faultTable.Split('.')[0];
        await context.Bills.AdminAsync($"CREATE TRIGGER {schema}.SupplierCommandFault ON {faultTable} AFTER INSERT AS THROW 51995,'Disposable supplier stage fault.',1;");
        try
        {
            // WHEN persistence fails at the selected actual source, journal or attribution write.
            var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync(operation, request, command));
            // THEN every financial row, item version, audit and receipt returns to its prior bytes.
            Assert.Equal(51995, error.Number);
            Assert.Equal(before, await SnapshotAsync(context));
        }
        finally { await context.Bills.AdminAsync($"DROP TRIGGER {schema}.SupplierCommandFault"); }
        // AND the identical request can commit once after the fault is removed.
        var result = await context.Bills.ExecuteAsync(operation, request, command);
        Assert.Equal(result.ToJsonString(), (await context.Bills.ExecuteAsync(operation, request, command)).ToJsonString());
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts WHERE RequestId='{request}'"));
        Assert.Equal(50m, await context.Allocation.BalanceAsync(bill));
    }

    internal static async Task<string> SnapshotAsync(SupplierPaymentTestContext context, bool includeSecurityAudit = true)
    {
        // Full persisted rows catch partial updates as well as leaked inserts; database rowversion counters are not transactional.
        var tables = new[] { "Accounting.SourceEvents", "Accounting.JournalEntries", "Accounting.JournalLines", "Accounting.PostingReceipts",
            "Accounting.PolicyFreezes", "Accounting.Periods", "Accounting.CorrectionGroups", "Accounting.CorrectionReceipts",
            "Purchasing.SupplierPayments", "Purchasing.SupplierPaymentVersions", "Purchasing.SupplierPaymentCorrections", "Purchasing.SupplierOpenItems", "Purchasing.SupplierItemVersions",
            "Purchasing.SupplierApplications", "Purchasing.SupplierItemMovements", "Purchasing.SupplierControlAttributions",
            "Purchasing.SupplierApplicationVersions", "Purchasing.SupplierApplicationReversals",
            "Purchasing.SupplierFinancialGroups", "Purchasing.SupplierFinancialReceipts", "Security.TenantSecurityAuditEvents" };
        var snapshots = new List<string>();
        foreach (var table in tables.Where(t => includeSecurityAudit || t != "Security.TenantSecurityAuditEvents"))
        {
            var rows = await context.Bills.ScalarAsync<string>($"SELECT COALESCE((SELECT * FROM {table} FOR JSON PATH,INCLUDE_NULL_VALUES),'[]')");
            snapshots.Add(string.Join('\n', JsonNode.Parse(rows)!.AsArray().Select(row => row!.ToJsonString()).Order(StringComparer.Ordinal)));
        }
        return string.Join('\n', snapshots);
    }
}
