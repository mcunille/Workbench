// Copyright (c) 2026 The White Stag Collection.
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Workbench.Server.Accounting;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierReconciliationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task TwoCutoffsReproduceSeptemberBalances()
    {
        // GIVEN a genuine deposit on September 10 and bill on September 15.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var payment = await context.CommandAsync("100", "2026-09-10");
        await context.RecordAsync(payment);
        var advance = Guid.Parse(payment["paymentId"]!.GetValue<string>());
        var bill = await context.Allocation.BillAsync("150");
        var before = await context.Bills.ScalarAsync<DateTimeOffset>("SELECT SYSDATETIMEOFFSET()");
        // WHEN an application is later recorded with a September 16 posting date.
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(advance, bill));
        var september14 = await Read(context, "?postingThrough=2026-09-14");
        var september15 = await Read(context, "?postingThrough=2026-09-15");
        var september16AsRecordedSeptember19 = await Read(context, $"?postingThrough=2026-09-16&recordedThrough={Uri.EscapeDataString(before.ToUniversalTime().ToString("O"))}");
        var september16AfterSeptember20Commit = await Read(context, "?postingThrough=2026-09-16");
        // THEN the two independent inclusive cutoffs reproduce all four balances.
        Assert.Equal(("100.00", "0.00"), Pair(september14));
        Assert.Equal(("100.00", "150.00"), Pair(september15));
        Assert.Equal(("100.00", "150.00"), Pair(september16AsRecordedSeptember19));
        Assert.Equal(("0.00", "50.00"), Pair(september16AfterSeptember20Commit));
        Assert.True(september16AfterSeptember20Commit.IsComplete);
    }


    [Fact]
    public async Task UnknownControlsPreventCompleteReconciliation()
    {
        // GIVEN two independently owned bills, one outside the selected bill filter.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var selected = await context.Allocation.BillAsync("50");
        var unknown = await context.Allocation.BillAsync("70");
        Assert.True((await Read(context)).IsComplete);
        // WHEN privileged corruption removes attribution for the other bill.
        await context.Bills.AdminAsync($"DELETE a FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId WHERE m.ItemId='{unknown}'");
        var scoped = await Read(context, $"?billId={selected}");
        // THEN unknown tenant activity is exposed without assigning it to the selected bill.
        Assert.False(scoped.IsComplete);
        Assert.True(scoped.UnresolvedTenantControlCount > 0);
        Assert.Equal("50.00", scoped.Controls.WholeFilterTotals.Payable);
        Assert.Contains(scoped.Controls.Items, c => c.MissingAttributionCount > 0);
    }

    [Fact]
    public async Task HistoricalMappingAndZeroNetDiscrepanciesRemainVisible()
    {
        // GIVEN all four control families, including a fully settled payment and debt.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100");
        var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
        await context.RecordAsync(payment);
        await context.Allocation.SourceAsync("CreditReceivable", "20");
        await context.Allocation.SourceAsync("RefundClearing", "30");
        var payable = context.Bills.Recognition.Accounts["SupplierPayable"];
        var newControl = await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """[{"code":"NEWAP7","name":"New payable","type":"Liability","purpose":"SupplierPayable"}]""");
        var newAccount = Guid.Parse(System.Text.Json.Nodes.JsonNode.Parse(newControl.Ids)![0]!.ToString());
        await context.Bills.AdminAsync($"""
            DECLARE @slot nvarchar(20)=(SELECT m.[key] FROM Accounting.Configurations c CROSS APPLY OPENJSON(c.Payload,'$.mappings') m WHERE JSON_VALUE(m.value,'$.slot')='SupplierPayable');
            UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,CONCAT('$.mappings[',@slot,'].accountId'),'{newAccount}');
            """);
        // WHEN current mapping/account state changes THEN immutable account-purpose evidence remains reportable.
        await context.Bills.AdminAsync($"UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Name='Archived control',Version=NEWID() WHERE Id='{payable}'");
        var valid = await Read(context);
        Assert.True(valid.IsComplete);
        Assert.Equal("20.00", valid.Controls.WholeFilterTotals.CreditReceivable);
        Assert.Equal("30.00", valid.Controls.WholeFilterTotals.RefundClearing);
        Assert.Contains(valid.Controls.Items, c => c.AccountId == payable && c.ControlFamily == "Payable");
        Assert.Equal(4, valid.Controls.Items.Select(c => c.ControlFamily).Distinct().Count());
        Assert.DoesNotContain(valid.Controls.Items, c => c.AccountId == newAccount);
        // WHEN all payable attribution is removed its journal still nets to zero.
        await context.Bills.AdminAsync("DELETE FROM Purchasing.SupplierControlAttributions WHERE AccountPurpose='SupplierPayable'");
        var corrupt = await Read(context);
        // THEN offsetting zero-net missing evidence cannot pass reconciliation.
        Assert.False(corrupt.IsComplete);
        Assert.True(corrupt.UnresolvedTenantControlCount >= 2);
        Assert.Contains(corrupt.Controls.Items, c => c.AccountId == payable && c.JournalAmount == "0.00" && !c.IsComplete);
    }

    [Fact]
    public async Task DuplicateAttributionIsIncompleteEvenWhenAggregateIsUnchanged()
    {
        // GIVEN a payable with authentic source and exact line ownership.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.Allocation.BillAsync("100");
        Assert.True((await Read(context)).IsComplete);
        // WHEN a privileged fault duplicates and offsets attribution on the same movement.
        await context.Bills.AdminAsync("""
            DROP INDEX IX_SupplierControlAttributions_TenantId_JournalId_Ordinal_MovementId ON Purchasing.SupplierControlAttributions;
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT TenantId,NEWID(),GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount FROM Purchasing.SupplierControlAttributions;
            UPDATE Purchasing.SupplierControlAttributions SET Amount=Amount/2;
            """);
        var result = await Read(context);
        // THEN preserving the line aggregate cannot disguise duplicate movement coverage.
        Assert.False(result.IsComplete);
        Assert.Contains(result.Controls.Items, c => c.DuplicateAttributionCount > 0);
    }

    [Fact]
    public async Task MixedPaymentCorrectionAndCompensationKeepTheirExactOwners()
    {
        // GIVEN embedded and standalone applications, plus a retained embedded unapplication.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150");
        var payment = await context.CommandAsync("120"); await context.AllocateAsync(payment, bill, "60");
        var posted = await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, bill, "30"));
        var embedded = Guid.Parse(posted["applicationIds"]![0]!.ToString());
        await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, embedded));
        Assert.True((await Read(context)).IsComplete);
        var before = await context.Bills.ScalarAsync<DateTimeOffset>("SELECT SYSDATETIMEOFFSET()");
        var replacement = await context.CommandAsync("80", "2026-09-20"); await context.AllocateAsync(replacement, bill, "50");
        // WHEN the mixed correction records original inverse, standalone inverse, compensation, and replacement.
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement));
        var current = await Read(context);
        // THEN each whole-group owner reconciles and earlier recorded history remains unchanged.
        Assert.True(current.IsComplete, System.Text.Json.JsonSerializer.Serialize(current));
        Assert.Equal(("30.00", "100.00"), Pair(current));
        var historical = await Read(context, $"?recordedThrough={Uri.EscapeDataString(before.ToUniversalTime().ToString("O"))}");
        Assert.True(historical.IsComplete);
        Assert.Equal(("90.00", "120.00"), Pair(historical));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationAndReversalSourceIdentitiesCannotDetach(bool reapply)
    {
        // GIVEN authentic standalone allocation and reversal, optionally with reapplication in the reversal group.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100");
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill, "60"));
        var application = Guid.Parse(applied["applicationIds"]![0]!.ToString());
        var reverse = await SupplierCorrectionFixture.ReverseAsync(context, application);
        if (reapply)
        {
            var retain = await context.Allocation.CommandAsync(funding, bill, "40", "2026-09-20");
            reverse["reapplications"] = new JsonArray(new JsonObject
            {
                ["fundingItemId"] = funding.ToString(),
                ["expectedFundingItemVersion"] = retain["expectedFundingItemVersion"]!.DeepClone(),
                ["billId"] = bill.ToString(),
                ["itemId"] = bill.ToString(),
                ["expectedItemVersion"] = retain["targets"]![0]!["expectedItemVersion"]!.DeepClone(),
                ["amount"] = "40"
            });
        }
        await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), reverse);
        var valid = await Read(context); Assert.True(valid.IsComplete);
        var sources = JsonNode.Parse(await context.Bills.ScalarAsync<string>("""
            SELECT Id,SourceId,SourceKind,EventKind,SourceRevision,PostingDate,RecordedAtUtc FROM Accounting.SourceEvents
            WHERE SourceKind IN('SupplierApplication','SupplierApplicationReversal') ORDER BY SourceKind,Id FOR JSON PATH
            """))!.AsArray();
        Assert.Equal(reapply ? 3 : 2, sources.Count);
        foreach (var source in sources)
        {
            var table = source!["SourceKind"]!.ToString() == "SupplierApplication" ? "SupplierApplications" : "SupplierApplicationReversals";
            var sourceWhere = $"WHERE Id='{source["Id"]}'";
            var ownerWhere = $"WHERE Id='{source["SourceId"]}'";
            var faults = new[]
            {
                ("Accounting.SourceEvents", "SourceRevision", "NEWID()", source["SourceRevision"]!.ToString(), sourceWhere),
                ("Accounting.SourceEvents", "SourceKind", "'PurchaseRecognition'", source["SourceKind"]!.ToString(), sourceWhere),
                ("Accounting.SourceEvents", "EventKind", "'Invoice'", source["EventKind"]!.ToString(), sourceWhere),
                ($"Purchasing.{table}", "PostingDate", "DATEADD(day,1,PostingDate)", source["PostingDate"]!.ToString(), ownerWhere),
                ($"Purchasing.{table}", "RecordedAtUtc", "DATEADD(second,-1,RecordedAtUtc)", source["RecordedAtUtc"]!.ToString(), ownerWhere)
            };
            foreach (var (faultTable, column, changed, original, where) in faults)
            {
                // WHEN exactly one source or owner identity changes while all journal/item arithmetic remains intact.
                await context.Bills.AdminAsync($"UPDATE {faultTable} SET {column}={changed} {where}");
                var corrupt = await Read(context);
                // THEN invalid-source readback identifies the detached evidence, even inside a composed group.
                Assert.False(corrupt.IsComplete, $"Accepted detached {source["SourceKind"]}.{column} (reapply={reapply})");
                Assert.Contains(corrupt.Controls.Items, c => c.InvalidSourceEvidenceCount > 0);
                Assert.Equal(Pair(valid), Pair(corrupt));
                await context.Bills.AdminAsync($"UPDATE {faultTable} SET {column}='{original}' {where}");
                Assert.True((await Read(context)).IsComplete);
            }
        }
    }

    [Fact]
    public async Task CompensationCannotOwnAStandaloneUnapplySharingItsPayment()
    {
        // GIVEN equal embedded and standalone applications sharing the same payment, bill and historical controls.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150");
        var payment = await context.CommandAsync("120"); await context.AllocateAsync(payment, bill, "60");
        var posted = await context.RecordAsync(payment); var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill, "60"));
        var embedded = Guid.Parse(posted["applicationIds"]![0]!.ToString());
        var standalone = Guid.Parse(applied["applicationIds"]![0]!.ToString());
        await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, embedded));
        await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, standalone));
        Assert.True((await Read(context)).IsComplete);
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, funding));
        var valid = await Read(context); Assert.True(valid.IsComplete);
        Assert.Equal(("0.00", "150.00"), Pair(valid));
        // WHEN a privileged fault redirects the valid embedded compensation to the standalone unapply's exact source/journal.
        await context.Bills.AdminAsync($"""
            DECLARE @standaloneSource uniqueidentifier,@standaloneJournal uniqueidentifier,@standaloneReversal uniqueidentifier,@correction uniqueidentifier;
            SELECT @standaloneSource=s.Id,@standaloneJournal=j.Id,@standaloneReversal=r.Id
              FROM Purchasing.SupplierApplicationReversals r JOIN Accounting.SourceEvents s ON s.SourceId=r.Id AND s.EventKind='Reverse'
              JOIN Accounting.JournalEntries j ON j.SourceEventId=s.Id WHERE r.ApplicationId='{standalone}';
            SELECT @correction=c.Id FROM Accounting.CorrectionGroups c JOIN Accounting.SourceEvents s ON s.Id=c.OriginalSourceEventId
              JOIN Purchasing.SupplierApplicationReversals r ON r.Id=s.SourceId WHERE r.ApplicationId='{embedded}';
            IF @standaloneSource IS NULL OR @correction IS NULL THROW 51000,'Fault requires both authentic unapplications and compensation.',1;
            IF EXISTS(SELECT Ordinal,AccountId,AccountVersion,AccountPurpose,Debit,Credit FROM Accounting.JournalLines WHERE JournalId=@standaloneJournal
              EXCEPT SELECT l.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,l.Debit,l.Credit FROM Accounting.JournalLines l
              JOIN Accounting.CorrectionGroups c ON c.OriginalJournalId=l.JournalId WHERE c.Id=@correction)
              THROW 51000,'Fault requires identical original historical lines.',1;
            UPDATE Accounting.CorrectionGroups SET OriginalSourceEventId=@standaloneSource,OriginalJournalId=@standaloneJournal WHERE Id=@correction;
            UPDATE s SET SourceId=@standaloneReversal,SnapshotJson=original.SnapshotJson,SnapshotSha256=original.SnapshotSha256
              FROM Accounting.SourceEvents s JOIN Accounting.CorrectionGroups c ON c.ReversalSourceEventId=s.Id
              JOIN Accounting.SourceEvents original ON original.Id=c.OriginalSourceEventId WHERE c.Id=@correction;
            UPDATE c SET EvidenceJson=e.Json,EvidenceSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),e.Json))
              FROM Accounting.CorrectionGroups c JOIN Accounting.SourceEvents s ON s.Id=c.OriginalSourceEventId
              CROSS APPLY(SELECT JSON_MODIFY(JSON_MODIFY(c.EvidenceJson,'$.originalSourceRevision',CONVERT(nvarchar(36),s.SourceRevision)),
                '$.originalSnapshotSha256',CONVERT(varchar(64),s.SnapshotSha256,2)) Json) e WHERE c.Id=@correction;
            """);
        var corrupt = await Read(context);
        // THEN funding identity and equal line/amount evidence cannot confer embedded compensation ownership.
        Assert.False(corrupt.IsComplete);
        Assert.Contains(corrupt.Controls.Items, c => c.InvalidSourceEvidenceCount > 0);
        Assert.Equal(Pair(valid), Pair(corrupt));
    }

    [Fact]
    public async Task RuntimeControlProofCannotReadAnotherTenant()
    {
        // GIVEN actual bill/payment proof exposed through only the two read-only grants.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync();
        await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
        Assert.True((await Read(context)).IsComplete);
        await using var other = await context.Allocation.Journal.OpenOtherTenantAsync();
        // WHEN the other runtime tenant explicitly requests the known tenant and identifiers.
        foreach (var (function, item) in new[] { ("SupplierItemControl", bill), ("SupplierPaymentControl", id) })
        {
            await using var sql = new Microsoft.Data.SqlClient.SqlCommand($"SELECT COUNT(*) FROM Purchasing.{function}(@tenant,@item)", other);
            sql.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); sql.Parameters.AddWithValue("@item", item);
            // THEN RLS still hides all proof rows through the function's ownership chain.
            Assert.Equal(0, (int)(await sql.ExecuteScalarAsync())!);
        }
    }


    [Fact]
    public async Task EqualValuedCorrectionLinesCannotExchangeItemOwnership()
    {
        // GIVEN two equal bills settled by a fully allocated payment and then corrected.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var first = await context.Allocation.BillAsync("60");
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET Version=NEWID(),Name='Later payable snapshot' WHERE Purpose='SupplierPayable'");
        var second = await context.Allocation.BillAsync("60");
        var payment = await context.CommandAsync("120"); await context.AllocateAsync(payment, first, "60"); await context.AllocateAsync(payment, second, "60");
        await context.RecordAsync(payment);
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString())));
        Assert.True((await Read(context)).IsComplete);
        // WHEN equal-valued debt movement ownership is exchanged across distinct inverse lines.
        await context.Bills.AdminAsync($"""
            DECLARE @first int,@second int,@journal uniqueidentifier;
            SELECT @journal=c.ReversalJournalId FROM Accounting.CorrectionGroups c JOIN Accounting.SourceEvents s ON s.Id=c.OriginalSourceEventId WHERE s.SourceKind='SupplierPayment';
            SELECT @first=a.Ordinal FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId WHERE a.JournalId=@journal AND m.ItemId='{first}';
            SELECT @second=a.Ordinal FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId WHERE a.JournalId=@journal AND m.ItemId='{second}';
            IF @first=@second OR @first IS NULL OR @second IS NULL THROW 51000,'Fault requires two distinct historical lines.',1;
            UPDATE a SET MovementId=other.Id FROM Purchasing.SupplierControlAttributions a
              JOIN Purchasing.SupplierItemMovements original ON original.Id=a.MovementId
              JOIN Purchasing.SupplierItemMovements other ON other.GroupId=original.GroupId AND other.SourceEventId=original.SourceEventId
                AND other.ItemId<>original.ItemId AND other.ItemId IN('{first}','{second}')
              WHERE a.JournalId=@journal AND original.ItemId IN('{first}','{second}');
            """);
        var corrupt = await Read(context);
        // THEN unchanged account totals cannot substitute for exact original movement/line ownership.
        Assert.False(corrupt.IsComplete);
        Assert.Contains(corrupt.Controls.Items, c => c.InvalidSourceEvidenceCount > 0);
    }

    [Fact]
    public async Task PostedZeroValueBillIsAValidEmptyFilter()
    {
        // GIVEN a real posted zero-value bill, which intentionally has no monetary item.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("0");
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE BillId='{bill}'"));
        // WHEN the runtime report validates its bill filter through the narrow identity projection.
        var result = await Read(context, $"?billId={bill}");
        // THEN it is an empty complete report, and another tenant cannot read the projection.
        Assert.True(result.IsComplete); Assert.Empty(result.Controls.Items);
        await using var other = await context.Allocation.Journal.OpenOtherTenantAsync();
        await using var sql = new Microsoft.Data.SqlClient.SqlCommand($"SELECT COUNT(*) FROM Purchasing.SupplierReportBillIdentity('{JournalTestContext.TenantId}','{bill}')", other);
        Assert.Equal(0, (int)(await sql.ExecuteScalarAsync())!);
    }


    [Fact]
    public async Task UnassignedLegacyRecognitionRequiresItsStoredSourceIdentity()
    {
        // GIVEN a genuine invoice-side recognition with no structured bill.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var command = await context.Bills.Recognition.CommandAsync(side: "Invoice", cost: "40", tax: "0");
        var posted = await context.Bills.Recognition.PostAsync(command.ToJsonString()); var id = posted.EventIds[0];
        Assert.True((await Read(context)).IsComplete);
        await using (var connection = await context.Allocation.Journal.OpenSiblingAsync())
        await using (var db = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(connection).Options, new TenantContext(JournalTestContext.TenantId)))
        {
            var result = await SupplierOpenItemQueries.Read(id, new DefaultHttpContext(), db, new EphemeralDataProtectionProvider(), default);
            var item = Assert.IsType<SupplierOpenItemSummary>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.Null(item.BillId); Assert.Equal(id, item.SourceId); Assert.Equal("PurchaseRecognition", item.SourceKind);
            Assert.Equal("40.00", item.Balance); Assert.True(item.HasValidSource);
        }
        // AND a production recognition replacement has its own durable correction owner.
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context.Bills.Recognition, command, "30");
        await context.Bills.Recognition.CorrectAsync(correction.ToJsonString());
        Assert.True((await Read(context)).IsComplete);
        var owner = await context.Bills.ScalarAsync<Guid>("SELECT SourceId FROM Purchasing.SupplierFinancialGroups WHERE Operation='CorrectSource'");
        await context.Bills.AdminAsync("UPDATE Purchasing.SupplierFinancialGroups SET SourceId=NEWID() WHERE Operation='CorrectSource'");
        // THEN matching timestamps and inverse amounts cannot substitute for the recorded correction owner.
        Assert.False((await Read(context)).IsComplete);
        await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierFinancialGroups SET SourceId='{owner}' WHERE Operation='CorrectSource'");
        Assert.True((await Read(context)).IsComplete);
        // WHEN privileged corruption detaches that legacy identity while keeping all amounts and control lines unchanged.
        await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierOpenItems SET SourceId=NEWID() WHERE Id='{id}'");
        var corrupt = await Read(context);
        // THEN an unproved legacy identity is an explicit source discrepancy, never an invented bill.
        Assert.False(corrupt.IsComplete); Assert.Contains(corrupt.Controls.Items, c => c.InvalidSourceEvidenceCount > 0);
    }

    internal static async Task<SupplierReconciliationSummary> Read(SupplierPaymentTestContext context, string query = "")
    {
        await using var connection = await context.Allocation.Journal.OpenSiblingAsync();
        await using var db = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(connection).Options,
            new TenantContext(JournalTestContext.TenantId));
        var http = new DefaultHttpContext(); http.Request.QueryString = new QueryString(query);
        var result = await SupplierReconciliationQueries.Reconcile(http, db, new EphemeralDataProtectionProvider(), default);
        return Assert.IsType<SupplierReconciliationSummary>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }
    private static (string, string) Pair(SupplierReconciliationSummary result) =>
        (result.Controls.WholeFilterTotals.Advance, result.Controls.WholeFilterTotals.Payable);
}
