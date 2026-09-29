// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierAllocationTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("CreditReceivable", "Payable")]
    [InlineData("Advance", "RefundClearing")]
    [InlineData("CreditReceivable", "RefundClearing")]
    public async Task RestrictedParticipantConservesAllSupportedControlPairs(string fundingKind, string debtKind)
    {
        // GIVEN immutable synthetic source capacity for future credit/refund owners and real bill capacity where required.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var funding = await context.SourceAsync(fundingKind);
        var debt = debtKind == "Payable" ? await context.BillAsync() : await context.SourceAsync(debtKind, "150", "2026-09-15");
        // WHEN the restricted source adapter invokes the real journal and shared participant THEN both sides conserve 100.
        await context.ParticipantAsync(funding, debt);
        Assert.Equal(0m, await context.BalanceAsync(funding)); Assert.Equal(50m, await context.BalanceAsync(debt));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId WHERE m.EventKind='Apply' AND a.Amount=-100"));
    }

    [Fact]
    public async Task HistoricalKernelRejectsUnprovedAccountException()
    {
        // GIVEN archived controls and a caller that supplies one unrelated item identity as its historical proof.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var funding = await context.SourceAsync(); var debt = await context.BillAsync();
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID() WHERE Id IN(SELECT AccountId FROM Purchasing.SupplierControlAttributions)");
        // WHEN the real kernel sees invalid proof THEN no historical exception or financial write is accepted.
        var before = await context.Journal.CountAsync("JournalEntries");
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ParticipantAsync(funding, debt, invalidProof: true));
        Assert.Equal(51004, error.Number); Assert.Contains("Historical supplier control evidence", error.Message);
        Assert.Equal(before, await context.Journal.CountAsync("JournalEntries"));
        // AND genuine immutable proof still works against the same archived account state.
        await context.ParticipantAsync(funding, debt);
        Assert.Equal(50m, await context.BalanceAsync(debt));
    }
    [Fact]
    public async Task PartialAllocationConservesBothItems()
    {
        // GIVEN an advance of 100 on September 10 and a posted bill of 150 on September 15.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(); var bill = await context.BillAsync();
        var command = await context.CommandAsync(advance, bill); var request = Guid.NewGuid();
        // WHEN 100 is applied on September 16 and the request is retried.
        var result = await context.ApplyAsync(command, request);
        // THEN both sides conserve capacity, one application and its genuine receipt are committed.
        Assert.Equal(0m, await context.BalanceAsync(advance));
        Assert.Equal(50m, await context.BalanceAsync(bill));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
        Assert.Equal(result.ToJsonString(), (await context.ApplyAsync(command, request)).ToJsonString());
        Assert.NotNull(result["groupId"]); Assert.Single(result["applicationIds"]!.AsArray());
        Assert.Single(result["journalIds"]!.AsArray()); Assert.Equal(2, result["itemVersions"]!.AsArray().Count);
    }

    [Fact]
    public async Task MultipleTargetsCommitOneInstantAndReceiptFailureRollsBackEverything()
    {
        // GIVEN two bills in explicit target order and a fault at the final receipt write.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(); var first = await context.BillAsync(); var second = await context.BillAsync("70");
        var command = await context.CommandAsync(advance, first, "60");
        command["targets"]!.AsArray().Add((await context.CommandAsync(advance, second, "40"))["targets"]![0]!.DeepClone());
        var before = await context.Journal.CountAsync("JournalEntries");
        await context.Bills.AdminAsync("CREATE TRIGGER Purchasing.FailAllocationReceipt ON Purchasing.SupplierFinancialReceipts AFTER INSERT AS THROW 51999,'Receipt fault',1;");
        try
        {
            // WHEN the late write fails THEN all earlier journals, applications and movements roll back together.
            Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() => context.ApplyAsync(command))).Number);
            Assert.Equal(before, await context.Journal.CountAsync("JournalEntries"));
            Assert.Equal(100m, await context.BalanceAsync(advance));
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
        }
        finally { await context.Bills.AdminAsync("DROP TRIGGER Purchasing.FailAllocationReceipt"); }
        // AND the unchanged command can commit in order, with one common group/source/journal/receipt instant.
        var result = await context.ApplyAsync(command);
        Assert.Equal(0m, await context.BalanceAsync(advance)); Assert.Equal(90m, await context.BalanceAsync(first)); Assert.Equal(30m, await context.BalanceAsync(second));
        Assert.Equal(first, await context.Bills.ScalarAsync<Guid>($"SELECT DebtItemId FROM Purchasing.SupplierApplications WHERE Id='{result["applicationIds"]![0]}'"));
        var group = result["groupId"]!.GetValue<string>();
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"""
            SELECT COUNT(DISTINCT RecordedAtUtc) FROM(
              SELECT RecordedAtUtc FROM Purchasing.SupplierFinancialGroups WHERE Id='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierApplications WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.SupplierFinancialReceipts WHERE GroupId='{group}'
              UNION ALL SELECT RecordedAtUtc FROM Accounting.SourceEvents WHERE SourceRevision='{group}'
              UNION ALL SELECT j.RecordedAtUtc FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s ON s.Id=j.SourceEventId WHERE s.SourceRevision='{group}'
              UNION ALL SELECT p.RecordedAtUtc FROM Accounting.PostingReceipts p JOIN Accounting.SourceEvents s ON s.Id=p.SourceEventId WHERE s.SourceRevision='{group}'
            ) instants;
            """));
    }

    [Fact]
    public async Task AllocationUsesHistoricalAccounts()
    {
        // GIVEN posted funding/debt followed by mapping, archive and account version changes.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(); var bill = await context.BillAsync();
        var oldPayable = context.Items.Recognition.Accounts["SupplierPayable"];
        var remapped = await context.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """[{"code":"NEWAP","name":"New payable","type":"Liability","purpose":"SupplierPayable"}]""");
        var newPayable = JsonNode.Parse(remapped.Ids)![0]!.GetValue<string>();
        var mappings = new JsonArray(new JsonObject { ["slot"] = "SupplierPayable", ["accountId"] = newPayable }).ToJsonString();
        await context.Bills.AdminAsync($"""
            UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.mappings',JSON_QUERY('{mappings}'));
            UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID(),Name='Archived control'
              WHERE Id IN(SELECT AccountId FROM Purchasing.SupplierControlAttributions);
            """);
        // WHEN an application posts THEN its debit/credit use the original account identities and snapshots.
        await context.ApplyAsync(await context.CommandAsync(advance, bill));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(oldPayable, await context.Bills.ScalarAsync<Guid>("SELECT l.AccountId FROM Accounting.JournalLines l JOIN Accounting.SourceEvents s ON s.Id=(SELECT SourceEventId FROM Accounting.JournalEntries WHERE Id=l.JournalId) WHERE s.SourceKind='SupplierApplication' AND l.Debit=100"));
        Assert.Equal("SupplierPayable", await context.Bills.ScalarAsync<string>("SELECT l.AccountName FROM Accounting.JournalLines l JOIN Accounting.JournalEntries j ON j.Id=l.JournalId JOIN Accounting.SourceEvents s ON s.Id=j.SourceEventId WHERE s.SourceKind='SupplierApplication' AND l.Debit=100"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId WHERE m.EventKind='Apply' AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions o JOIN Purchasing.SupplierItemMovements opening ON opening.Id=o.MovementId WHERE opening.ItemId=m.ItemId AND opening.EventKind='Open' AND o.AccountId=a.AccountId AND o.AccountVersion=a.AccountVersion)"));
    }

    [Theory]
    [InlineData("non-bill", 51004)]
    [InlineData("future-source", 51004)]
    [InlineData("future-funding", 51004)]
    [InlineData("cross-currency", 51004)]
    [InlineData("cross-po", 51004)]
    [InlineData("duplicate", 51000)]
    [InlineData("stale", 51009)]
    [InlineData("precision", 51000)]
    [InlineData("scale", 51000)]
    [InlineData("zero", 51000)]
    [InlineData("overflow", 51000)]
    [InlineData("unknown", 51000)]
    public async Task InvalidApplicationsLeaveAllFinancialEvidenceUnchanged(string scenario, int expectedError)
    {
        // GIVEN genuine posted bill and advance evidence, with one independently invalid request condition.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(date: scenario == "future-funding" ? "2026-09-17" : "2026-09-10"); var bill = await context.BillAsync();
        var command = await context.CommandAsync(advance, bill);
        if (scenario == "non-bill")
        {
            var invoice = await context.Items.Recognition.CommandAsync("Invoice", cost: "150");
            var posted = await context.Items.Recognition.PostAsync(invoice.ToJsonString());
            command["targets"]![0]!["itemId"] = posted.EventIds[0].ToString();
            command["targets"]![0]!["expectedItemVersion"] = await context.VersionAsync(posted.EventIds[0]);
        }
        if (scenario == "future-source") command["postingDate"] = "2026-09-14";
        if (scenario == "cross-currency") await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierOpenItems SET Currency='CAD' WHERE Id='{bill}'");
        if (scenario == "cross-po") await context.Bills.AdminAsync($"""
            DECLARE @Other uniqueidentifier=NEWID();
            INSERT Purchasing.DraftOrders(Id,TenantId,IsDeleted,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
              SELECT @Other,TenantId,IsDeleted,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId
              FROM Purchasing.DraftOrders WHERE Id='{context.Items.Recognition.PurchaseOrderId}';
            UPDATE Purchasing.SupplierOpenItems SET PurchaseOrderId=@Other WHERE Id='{bill}';
            """);
        if (scenario == "duplicate") command["targets"]!.AsArray().Add(command["targets"]![0]!.DeepClone());
        if (scenario == "stale") command["expectedFundingItemVersion"] = "0x0000000000000000";
        if (scenario == "precision") command["targets"]![0]!["amount"] = "0.00001";
        if (scenario == "scale") command["targets"]![0]!["amount"] = "0.001";
        if (scenario == "zero") command["targets"]![0]!["amount"] = "0";
        if (scenario == "overflow") command["targets"]![0]!["amount"] = "1000000000000000000000000";
        if (scenario == "unknown") command["paymentId"] = Guid.NewGuid().ToString();
        var before = await Counts();
        // WHEN production Apply processes it THEN the owning guard rejects it without journal, movement or receipt fragments.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ApplyAsync(command));
        Assert.Equal(expectedError, error.Number);
        Assert.Equal(before, await Counts());
        Task<string> Counts() => context.Bills.ScalarAsync<string>("SELECT CONCAT((SELECT COUNT(*) FROM Accounting.JournalEntries),':',(SELECT COUNT(*) FROM Purchasing.SupplierItemMovements),':',(SELECT COUNT(*) FROM Purchasing.SupplierApplications),':',(SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts))");
    }

    [Fact]
    public async Task SameDateApplicationsUseFinalCapacityAndClosedMonthsRejectNewWrites()
    {
        // GIVEN same-date sources with capacity for two applications.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(date: "2026-09-15"); var bill = await context.BillAsync();
        // WHEN two groups settle on that source date THEN exactly 100 is consumed; closed-month writes then fail.
        await context.ApplyAsync(await context.CommandAsync(advance, bill, "40", "2026-09-15"));
        await context.ApplyAsync(await context.CommandAsync(advance, bill, "60", "2026-09-15"));
        Assert.Equal(0m, await context.BalanceAsync(advance)); Assert.Equal(50m, await context.BalanceAsync(bill));
        var secondAdvance = await context.SourceAsync();
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using var close = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@config,@PeriodStart='2026-09-01',@Reason=N'Reviewed month',@Evidence=NULL", context.Journal.Connection);
        close.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); close.Parameters.AddWithValue("@session", context.Journal.SessionId);
        close.Parameters.AddWithValue("@request", Guid.NewGuid()); close.Parameters.AddWithValue("@config", context.Journal.ConfigurationVersion);
        await close.ExecuteNonQueryAsync();
        var command = await context.CommandAsync(secondAdvance, bill, "10");
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ApplyAsync(command));
        Assert.Equal(51009, error.Number); Assert.Contains("period is closed", error.Message);
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
    }

    [Fact]
    public async Task TargetCountBoundaryIsIndependentOfByteCap()
    {
        // GIVEN compact valid target identities with a 1000/1001 boundary below the byte cap.
        await using var context = await SupplierOpenItemTestContext.OpenAsync(sqlServer);
        var command = new JsonObject { ["schemaVersion"] = 1, ["operation"] = "ApplySupplierFunds", ["targets"] = new JsonArray() };
        var targets = command["targets"]!.AsArray();
        for (var index = 0; index < 1000; index++) targets.Add(new JsonObject { ["itemId"] = Guid.NewGuid().ToString(), ["amount"] = "1" });
        await context.ValidateAsync("ApplySupplierFunds", command);
        targets.Add(new JsonObject { ["itemId"] = Guid.NewGuid().ToString(), ["amount"] = "1" });
        Assert.True(System.Text.Encoding.Unicode.GetByteCount(command.ToJsonString()) < 262144);
        // WHEN one more target is supplied THEN count validation rejects it, independently of envelope size.
        var count = await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", command));
        Assert.Contains("targets", count.Message);
        targets.RemoveAt(1000); command["notes"] = new string('x', 131073);
        var bytes = await Assert.ThrowsAsync<SqlException>(() => context.ValidateAsync("ApplySupplierFunds", command));
        Assert.Contains("envelope", bytes.Message);
    }
}
