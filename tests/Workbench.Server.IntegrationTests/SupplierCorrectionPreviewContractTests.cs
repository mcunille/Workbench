// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCorrectionPreviewContractTests(SqlServerFixture server, SupplierCashClearingFixture clearing)
    : IClassFixture<SupplierCashClearingFixture>
{
    [Theory]
    [InlineData("Bill")]
    [InlineData("Credit")]
    [InlineData("Refund")]
    [InlineData("RefundRepayment")]
    public async Task SuccessfulPreviewPreservesCallerWorkAndPersistentState(string kind)
    {
        // GIVEN a valid correction on a date for which no period exists, inside a caller transaction.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        if (kind == "Bill") await context.EnableBillCorrectionsAsync();
        var source = kind switch
        {
            "Bill" => await context.InvoiceOnlyBillAsync("100"),
            "Credit" => prepared.History.SuccessorCredit,
            "RefundRepayment" => Guid.Parse((await context.RepaymentAsync([(prepared.History.Clearing, "8")]))["sourceId"]!.ToString()),
            _ => prepared.History.Refund
        };
        var input = await context.CorrectionCommandAsync(kind, source, null, "2026-10-01");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        await using var begin = new SqlCommand("""
            CREATE TABLE #CallerWork(Value int);
            BEGIN TRANSACTION;
            INSERT #CallerWork VALUES (17);
            """, context.Bills.Journal.Connection);
        await begin.ExecuteNonQueryAsync();
        try
        {
            // WHEN the successful preview returns THEN it preserves the transaction and leaves October unmaterialized.
            var plan = await context.PreviewCorrectionAsync(kind, input);
            await using var check = new SqlCommand("SELECT Value FROM #CallerWork WHERE @@TRANCOUNT=1;", context.Bills.Journal.Connection);
            Assert.Equal(17, (int)(await check.ExecuteScalarAsync())!);
            Assert.NotEmpty(Assert.IsType<JsonArray>(plan["proposedJournals"]));
            if (kind == "Refund")
            {
                Assert.Equal(0m, RemainingBalance(plan, prepared.History.Clearing));
                Assert.Equal(10m, RemainingBalance(plan, prepared.History.SuccessorCredit));
            }
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.Periods WHERE PeriodStart='2026-10-01'"));
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK;", context.Bills.Journal.Connection);
            await rollback.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }

    [Fact]
    public async Task CashCorrectionCannotReverseAndReplaceOnDifferentPostingDates()
    {
        // GIVEN a recorded refund and a feasible successor one posting day after its proposal's inverse.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var replacement = await context.RefundCommandAsync([(prepared.History.SuccessorCredit, "10")], "2026-10-01");
        var input = await context.CorrectionCommandAsync("Refund", prepared.History.Refund, replacement, "2026-09-30");

        // WHEN previewing THEN the mismatched date rejects before any financial changes.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Refund", input))).Number);
        Assert.Equal(18m, await context.BankBalanceAsync());
    }

    [Fact]
    public async Task CreditPreviewReportsTheNewClearingBalanceAndRequiresOnePostingDate()
    {
        // GIVEN an actual refund of a credit of 18 and a proposed correction to 10 with explicit reapplication.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var bill = await context.ExpenseBillAsync("100");
        var credit = Guid.Parse((await context.CreditAsync(bill, "18"))["sourceId"]!.ToString());
        var refund = await context.RefundAsync([(credit, "18")]);
        var clearingId = Guid.Parse(refund["settlementIds"]![0]!.ToString());
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", credit, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = replacement["creditId"]!.DeepClone(),
            ["debtItemId"] = clearingId.ToString(),
            ["amount"] = "10"
        });

        // WHEN previewing the complete group THEN the prepared clearing position is 8, without materializing a liability.
        var plan = await context.PreviewCorrectionAsync("Credit", command);
        Assert.Equal(8m, RemainingBalance(plan, clearingId));
        Assert.Equal("2026-09-16", plan["sources"]![0]!["replacement"]!["effectiveDate"]!.GetValue<string>());
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE Id='{clearingId}'"));

        // AND a different replacement date cannot expose only part of the group at a posting cutoff.
        command["replacement"]!["postingDate"] = "2026-09-21";
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
    }

    [Theory]
    [InlineData("Bill")]
    [InlineData("Credit")]
    public async Task RejectedPreviewLeavesCallerWorkCommittable(string kind)
    {
        // GIVEN an earlier write inside the caller's transaction and authority to preview a correction.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        if (kind == "Bill") await context.EnableBillCorrectionsAsync();
        var input = await context.CorrectionCommandAsync(kind,
            kind == "Bill" ? prepared.History.Bill : prepared.History.SuccessorCredit, null);
        input["postingDate"] = "invalid";
        var connection = context.Bills.Journal.Connection;
        await using var begin = new SqlCommand("CREATE TABLE #EarlierWrite(Value int); BEGIN TRAN; INSERT #EarlierWrite VALUES (17);", connection);
        await begin.ExecuteNonQueryAsync();
        try
        {
            // WHEN validation rejects the preview THEN the caller can still commit and read its earlier write.
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync(kind, input))).Number);
            await using var commit = new SqlCommand("COMMIT; SELECT Value FROM #EarlierWrite;", connection);
            Assert.Equal(17, (int)(await commit.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK;", connection);
            await rollback.ExecuteNonQueryAsync();
        }
    }

    internal static decimal RemainingBalance(JsonObject plan, Guid itemId)
    {
        var position = Assert.Single(plan["remainingPositions"]!.AsArray(),
            row => Guid.Parse(row!["itemId"]!.GetValue<string>()) == itemId);
        return decimal.Parse(position!["balance"]!.GetValue<string>(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task DisagreementWithTheApprovedJournalRollsBackTheCompleteCorrection()
    {
        // GIVEN an approved repayment correction and a disposable validator observing a different predicted line.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var repayment = await context.RepaymentAsync([(prepared.History.Clearing, "8")]);
        var input = await context.CorrectionCommandAsync("RefundRepayment", Guid.Parse(repayment["sourceId"]!.ToString()), null, "2026-09-22");
        await context.CaptureCashCorrectionPlanAsync("RefundRepayment", input);
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.ValidateSupplierSourceCorrectionGroup'));
            IF CHARINDEX(N'DECLARE @Expected TABLE',@definition)=0 THROW 50000,'Expected correction validator boundary missing.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,N'DECLARE @Expected TABLE',
              N'SET @Plan=JSON_MODIFY(@Plan,''$.proposedJournals[0].lines[0].accountVersion'',CONVERT(nvarchar(36),NEWID()));
                DECLARE @Expected TABLE');
            EXEC sys.sp_executesql @definition;
            """);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);

        // WHEN all writes would otherwise succeed THEN parity rejects the difference before finalizing the receipt.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("RefundRepayment", input));
        Assert.Equal(51004, error.Number);
        Assert.Contains("approved journal", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        Assert.Equal(0m, await context.BalanceAsync(prepared.History.Clearing));
        Assert.Equal(10m, await context.BankBalanceAsync());
    }

    [Fact]
    public async Task ChangedSuccessorAccountRevisionMakesTheApprovedLinesStale()
    {
        // GIVEN a replacement credit planned against the current receivable account revision.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var replacement = await context.CreditCommandAsync(prepared.History.Bill, "9", "2026-09-23");
        var input = await context.CorrectionCommandAsync("Credit", prepared.History.SuccessorCredit, replacement, "2026-09-23");
        await context.CaptureCorrectionPlanAsync(input);
        var priorVersion = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{context.CreditAccount}'");
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{context.CreditAccount}'");

        // WHEN the current receivable account is renamed THEN execution rejects the stale version in the preview.
        var updated = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new JsonObject { ["code"] = code, ["name"] = "Renamed supplier credit control" }.ToJsonString(),
            context.CreditAccount, priorVersion);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", input))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));

        input.Remove("expectedPlanFingerprint");
        var refreshed = await context.PreviewCorrectionAsync("Credit", input);
        var successor = Assert.Single(refreshed["proposedJournals"]!.AsArray(),
            journal => journal!["memberKind"]!.ToString() == "Replacement");
        Assert.Equal(updated.Version, Guid.Parse(successor!["lines"]![0]!["accountVersion"]!.ToString()));
    }

    [Fact]
    public async Task JournalExpansionCountsAgainstTheCompletePlanByteBound()
    {
        // GIVEN genuine recorded history and a disposable planner filling the unused space before journal expansion.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var input = await context.CorrectionCommandAsync("Credit", prepared.History.SuccessorCredit, null, "2026-09-23");
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PlanSupplierSourceCorrection')),
              @boundary nvarchar(120)=N'EXEC Purchasing.CompleteSupplierCorrectionJournalPlan';
            IF CHARINDEX(@boundary,@definition)=0 THROW 50000,'Expected journal planning boundary missing.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,@boundary,N'
              SET @Plan=JSON_MODIFY(@Plan,''$.verificationPadding'',REPLICATE(CONVERT(nvarchar(max),N''x''),(261850-DATALENGTH(@Plan))/2));
              IF DATALENGTH(@Plan) NOT BETWEEN 261850 AND 262000 THROW 50000,''Correction byte-bound fixture is invalid.'',1;
              '+@boundary);
            EXEC sys.sp_executesql @definition;
            """);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);

        // WHEN journal rows bring the complete plan over the cap THEN preview rejects the whole correction.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", input));
        Assert.Equal(51000, error.Number);
        Assert.Contains("byte bound", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }

    internal static async Task AssertJournalLinesAsync(SupplierCreditTestContext context, JsonObject plan, JsonObject result)
    {
        var group = Guid.Parse(result["groupId"]!.GetValue<string>());
        var stored = JsonNode.Parse(await context.Bills.ScalarAsync<string>($"""
            SELECT (SELECT member.Kind memberKind,source.SourceKind sourceKind,source.SourceId sourceId,
              COALESCE(original.SourceRevision,source.SourceRevision) sourceRevisionId,correction.OriginalJournalId originalJournalId,
              member.RecognitionEventId recognitionId,member.EntityId entityId,
              original.SourceKind originalSourceKind,original.SourceId originalSourceId,
              reversal.ApplicationId reversedApplicationId,historical.JournalId reversedApplicationJournalId,
              JSON_VALUE(source.SnapshotJson,'$.adjustment.sourceId') adjustmentCreditId,
              JSON_VALUE(source.SnapshotJson,'$.adjustment.recognitionEventId') adjustmentRecognitionId,
              application.FundingItemId fundingItemId,application.DebtItemId debtItemId,
              TRY_CONVERT(int,JSON_VALUE(member.EffectsJson,'$.position')) position,
              journal.PostingDate postingDate,journal.Currency currency,
              JSON_QUERY((SELECT line.Ordinal ordinal,line.AccountId accountId,line.AccountVersion accountVersion,
                CONVERT(varchar(60),line.Debit) debit,CONVERT(varchar(60),line.Credit) credit
                FROM Accounting.JournalLines line WHERE line.TenantId=journal.TenantId AND line.JournalId=journal.Id
                ORDER BY line.Ordinal FOR JSON PATH)) lines
            FROM Purchasing.SupplierSourceCorrectionMembers member
            JOIN Accounting.JournalEntries journal ON journal.TenantId=member.TenantId AND journal.Id=member.JournalId
            JOIN Accounting.SourceEvents source ON source.TenantId=journal.TenantId AND source.Id=journal.SourceEventId
            LEFT JOIN Accounting.CorrectionGroups correction ON correction.TenantId=member.TenantId AND correction.ReversalJournalId=journal.Id
            LEFT JOIN Accounting.JournalEntries predecessor ON predecessor.TenantId=correction.TenantId AND predecessor.Id=correction.OriginalJournalId
            LEFT JOIN Accounting.SourceEvents original ON original.TenantId=predecessor.TenantId AND original.Id=predecessor.SourceEventId
            LEFT JOIN Purchasing.SupplierApplications application ON application.TenantId=member.TenantId
              AND application.GroupId=member.GroupId AND application.Id=member.EntityId
            LEFT JOIN Purchasing.SupplierApplicationReversals reversal ON reversal.TenantId=member.TenantId
              AND reversal.GroupId=member.GroupId AND reversal.Id=source.SourceId AND member.Kind='ReverseApplication'
            LEFT JOIN Purchasing.SupplierApplications earlier ON earlier.TenantId=reversal.TenantId AND earlier.Id=reversal.ApplicationId
            OUTER APPLY(SELECT prior.Id JournalId FROM Accounting.JournalEntries prior
              JOIN Accounting.SourceEvents event ON event.TenantId=prior.TenantId AND event.Id=prior.SourceEventId
              WHERE prior.TenantId=earlier.TenantId AND prior.RecordedAtUtc=earlier.RecordedAtUtc AND prior.PostingDate=earlier.PostingDate
                AND (event.SourceKind='SupplierApplication' AND event.EventKind='Apply' AND event.SourceId=earlier.Id
                  OR event.SourceKind='SupplierPayment' AND event.EventKind='Payment' AND event.SourceId=earlier.FundingItemId
                    AND EXISTS(SELECT 1 FROM OPENJSON(event.SnapshotJson,'$.allocations') allocation
                      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(allocation.value,'$.applicationId'))=earlier.Id))) historical
            WHERE member.GroupId='{group}' FOR JSON PATH)
            """))!.AsArray();
        static (string Key, string Journal) Signature(JsonNode? row, bool planned)
        {
            var entry = row!.AsObject();
            var lines = entry["lines"]!.AsArray().Select(line =>
                $"{line!["ordinal"]}|{Guid.Parse(line["accountId"]!.ToString()):D}|{Guid.Parse(line["accountVersion"]!.ToString()):D}|{line["debit"]}|{line["credit"]}");
            string Key(string name) => Guid.TryParse(entry[name]?.ToString(), out var id) ? id.ToString("D") : "";
            var member = entry["memberKind"]!.ToString();
            var key = $"{member}|{entry["sourceKind"]}|";
            if (planned)
                key += $"{Key("sourceId")}|{Key("sourceRevisionId")}|{Key("originalJournalId")}|{Key("ownerMemberId")}|{entry["position"]}";
            else
            {
                key += member switch
                {
                    "Application" => $"{Key("fundingItemId")}|||{Key("debtItemId")}|{entry["position"]}",
                    "ReverseApplication" => $"||{Key("reversedApplicationJournalId")}|{Key("reversedApplicationId")}|",
                    "RecognitionAdjustment" => $"{Key("adjustmentCreditId")}|||{Key("adjustmentRecognitionId")}|",
                    "ReleaseSettlement" => $"{Key("sourceId")}|||{Key("entityId")}|",
                    "ReverseSource" when entry["originalSourceKind"]?.ToString() == "SupplierRefundRelease" =>
                        $"{Key("sourceId")}|{Key("sourceRevisionId")}|{Key("originalJournalId")}|{Key("originalSourceId")}|",
                    _ => $"{Key("sourceId")}|{Key("sourceRevisionId")}|{Key("originalJournalId")}|{Key("recognitionId")}|"
                };
            }
            return (key, $"{entry["postingDate"]}|{entry["currency"]}|{string.Join(";", lines)}");
        }
        var posted = stored.Select(row => Signature(row, planned: false)).ToArray();
        var proposed = plan["proposedJournals"]!.AsArray().Select(row => Signature(row, planned: true)).ToArray();
        Assert.Equal(posted.Length, posted.Select(row => row.Key).Distinct().Count());
        Assert.Equal(proposed.Length, proposed.Select(row => row.Key).Distinct().Count());
        Assert.Equal(posted.OrderBy(row => row.Key, StringComparer.Ordinal), proposed.OrderBy(row => row.Key, StringComparer.Ordinal));
    }
}
