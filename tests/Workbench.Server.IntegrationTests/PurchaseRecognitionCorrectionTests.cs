// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionCorrectionTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CorrectionKeepsOriginalTypedEvidenceReachableAfterRecoveryLoss(bool replace, bool repeatsOriginal)
    {
        // GIVEN authentic typed recognition evidence and a later recovery disposition for its bytes.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var bills = new SupplierBillTestContext(context);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(bills, storage);
        var original = await context.CommandAsync(); var side = original["units"]![0]!["sides"]![0]!;
        side["evidence"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        await StoreAsync(context, side); await context.PostAsync(original.ToJsonString());
        var originalSet = await bills.ScalarAsync<Guid>("SELECT Id FROM Accounting.FinancialEvidenceSets");
        var links = await bills.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH");
        await bills.AdminAsync($"INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc) VALUES('{JournalTestContext.TenantId}','{document.Revision}',NEWID(),1,'Missing',SYSUTCDATETIME())");
        var correction = await CorrectionAsync(context, original, replace ? "90" : null);
        if (repeatsOriginal)
        {
            var successorSide = correction["replacement"]!["sides"]![0]!;
            successorSide["evidence"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
            var fabricated = correction.DeepClone().AsObject(); var fabricatedSide = fabricated["replacement"]!["sides"]![0]!;
            fabricatedSide["evidence"]!["documents"]![0]!["revisionId"] = Guid.NewGuid().ToString();
            await StoreAsync(context, fabricatedSide);
            Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(fabricated.ToJsonString()))).Number);
            await StoreAsync(context, successorSide);
        }
        // WHEN reversing or replacing the source THEN historical protection survives without reacquiring old bytes.
        await context.CorrectAsync(correction.ToJsonString());
        Assert.Equal(links, await bills.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH"));
        Assert.True(await bills.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        if (replace) Assert.Equal(originalSet, await bills.ScalarAsync<Guid>("SELECT InheritedEvidenceSetId FROM Accounting.FinancialEvidenceSets WHERE InheritedEvidenceSetId IS NOT NULL"));
    }

    [Fact]
    public async Task MatchedUnitCorrectionRebuildsBothSides()
    {
        // GIVEN receipt 100 and invoice 105, whose immutable evidence and February balances are retained.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await MatchedAsync(context);
        var before = await SnapshotAsync(context);
        var correction = await CorrectionAsync(context, original, "98");
        await AdminAsync(context, "INSERT Accounting.PeriodClosures(TenantId,PeriodStart,Id,ActorId,Reason,EvidenceJson,EvidenceSha256,RecordedAtUtc) VALUES(@tenant,'2026-02-01',NEWID(),@actor,'Reconciled','{}',HASHBYTES('SHA2_256',N'{}'),SYSUTCDATETIME())", ("@tenant", JournalTestContext.TenantId), ("@actor", JournalTestContext.ActorId));
        var request = Guid.NewGuid();
        // WHEN the complete matched unit is replaced in March THEN inverse and replacement journals commit as one group.
        var result = await context.CorrectAsync(correction.ToJsonString(), request);
        Assert.NotNull(result.CorrectionGroupId);
        Assert.Equal(4, result.JournalIds.Length);
        Assert.Equal(2, result.EventIds.Length);
        Assert.Equal(2, result.MatchIds.Length);
        Assert.Equal(98m, await context.BalanceAsync("Inventory"));
        Assert.Equal(-98m, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(before, await SnapshotAsync(context, "WHERE e.PostingDate='2026-02-01'"));
        Assert.Equal(105m, await ScalarAsync<decimal>(context, "SELECT SUM(l.Debit-l.Credit) FROM Accounting.JournalLines l JOIN Accounting.JournalEntries j ON j.Id=l.JournalId JOIN Accounting.Accounts a ON a.Id=l.AccountId WHERE j.PostingDate<'2026-03-01' AND a.Name='Inventory'"));
        Assert.Equal(2, await ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.RecognitionEventCorrections WHERE ReplacementEventId IS NOT NULL AND AccountingCorrectionGroupId IS NOT NULL"));
        Assert.Equal(0, await ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.RecognitionEventCorrections c JOIN Accounting.CorrectionGroups g ON g.Id=c.AccountingCorrectionGroupId JOIN Accounting.JournalLines a ON a.JournalId=g.OriginalJournalId JOIN Accounting.JournalLines b ON b.JournalId=g.ReversalJournalId AND b.Ordinal=a.Ordinal WHERE a.AccountId<>b.AccountId OR a.Debit<>b.Credit OR a.Credit<>b.Debit"));
        Assert.Equal(0, await ScalarAsync<int>(context, "SELECT COUNT(*) FROM Accounting.JournalEntries WHERE PostingDate='2026-03-01' AND EffectiveDate<>'2026-02-01'"));
        Assert.Equal(2, await context.CountAsync("RecognitionGroupReceipts"));
        var replay = await context.CorrectAsync(correction.ToJsonString(), request);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(replay));
        await AssertGroupTimeAsync(context, result);
    }

    [Theory]
    [InlineData("100", false)]
    [InlineData("0", false)]
    [InlineData("0", true)]
    public async Task ReverseAppendsHistoryWithoutInventingZeroJournals(string cost, bool matched)
    {
        // GIVEN a single independent side or an all-zero matched unit.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = matched ? await MatchedAsync(context, "0", "0") : await context.CommandAsync(cost: cost);
        if (!matched) await context.PostAsync(original.ToJsonString());
        var before = await SnapshotAsync(context);
        var correction = await CorrectionAsync(context, original, null);
        // WHEN reversed THEN immutable side/match history remains and no zero journal is created.
        var result = await context.CorrectAsync(correction.ToJsonString());
        Assert.Equal(before, await SnapshotAsync(context));
        Assert.Empty(result.EventIds);
        Assert.Equal(cost == "0" ? 0 : 1, result.JournalIds.Length);
        Assert.Equal(matched ? 2 : 1, await context.CountAsync("RecognitionEventCorrections"));
        Assert.Equal(0m, await context.BalanceAsync("Inventory"));
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(correction.ToJsonString()))).Number);
    }

    [Fact]
    public async Task ReplacementCanBeCorrectedAgainWithItsOwnRevisions()
    {
        // GIVEN a matched unit already corrected to a fresh immutable successor.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await MatchedAsync(context);
        var first = await CorrectionAsync(context, original, "98");
        await context.CorrectAsync(first.ToJsonString());
        var successor = original.DeepClone().AsObject();
        successor["units"] = new JsonArray(first["replacement"]!.DeepClone());
        var second = await CorrectionAsync(context, successor, "90", "Expense");
        // WHEN the successor is replaced with approved expense classification THEN history links both generations.
        await context.CorrectAsync(second.ToJsonString());
        Assert.Equal(0m, await context.BalanceAsync("Inventory"));
        Assert.Equal(90m, await context.BalanceAsync("Expense"));
        Assert.Equal(-90m, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(2, await context.CountAsync("RecognitionCorrectionGroups"));
        Assert.Equal(4, await context.CountAsync("RecognitionEventCorrections"));
    }

    [Theory]
    [InlineData("stale", 51009)]
    [InlineData("partial", 51009)]
    [InlineData("early", 51000)]
    [InlineData("source", 51009)]
    [InlineData("effective", 51009)]
    [InlineData("same-revision", 51009)]
    [InlineData("same-unit", 51009)]
    [InlineData("disposal", 51009)]
    [InlineData("application", 51009)]
    [InlineData("source-permission", 51003)]
    [InlineData("forged-evidence", 51004)]
    [InlineData("unknown", 51000)]
    [InlineData("duplicate", 51000)]
    public async Task InvalidCorrectionCannotLeavePartialHistory(string defect, int number)
    {
        // GIVEN a complete matched unit and one stale, incomplete, unauthorized or unsupported correction.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await MatchedAsync(context);
        var correction = await CorrectionAsync(context, original, "98");
        var side = correction["replacement"]!["sides"]![0]!;
        switch (defect)
        {
            case "stale": correction["expectedEventRevisions"]![0]!["eventRevision"] = 9; break;
            case "partial": correction["expectedEventRevisions"]!.AsArray().RemoveAt(1); break;
            case "early": correction["postingDate"] = "2026-01-31"; break;
            case "source":
                var other = await context.CommandAsync(cost: "98");
                correction["replacement"]!["sides"]![0] = other["units"]![0]!["sides"]![0]!.DeepClone();
                correction["replacement"]!["sides"]![0]!["eventRevision"] = 2;
                break;
            case "effective": side["effectiveDate"] = "2026-01-31"; break;
            case "same-revision":
                side["sourceRevision"] = original["units"]![0]!["sides"]![0]!["sourceRevision"]!.DeepClone();
                await StoreAsync(context, side);
                await context.PublishSourceRevisionAsync(side);
                break;
            case "same-unit": correction["replacement"]!["unitId"] = original["units"]![0]!["unitId"]!.DeepClone(); break;
            case "disposal":
            case "application":
                await AdminAsync(context, "UPDATE Purchasing.FixtureRecognitionSources SET DependencyKind=@kind WHERE Id=@id", ("@kind", defect), ("@id", side["sourceId"]!.GetValue<string>())); break;
            case "source-permission": await AdminAsync(context, "UPDATE Purchasing.FixtureRecognitionSources SET CorrectionAllowed=0 WHERE Id=@id", ("@id", side["sourceId"]!.GetValue<string>())); break;
            case "forged-evidence": side["evidence"]!["rationale"] = "Unreviewed"; break;
            case "unknown": correction["noDependencies"] = true; break;
        }
        var json = correction.ToJsonString();
        if (defect == "duplicate") json = json.Insert(1, "\"reason\":\"other\",");
        // WHEN rejected THEN both original journals remain and the new period/group/receipt do not exist.
        Assert.Equal(number, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(json))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionCorrectionGroups"));
        Assert.Equal(0, await context.CountAsync("RecognitionEventCorrections"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(2, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(1, await context.Journal.CountAsync("Periods"));
        Assert.Equal(105m, await context.BalanceAsync("Inventory"));
    }

    [Theory]
    [InlineData("RecognitionEventCorrections", "1=1")]
    [InlineData("RecognitionSideEvents", "EXISTS(SELECT 1 FROM inserted WHERE CorrectionGroupId IS NOT NULL)")]
    [InlineData("RecognitionMatches", "EXISTS(SELECT 1 FROM inserted WHERE CorrectionGroupId IS NOT NULL)")]
    [InlineData("RecognitionGroupReceipts", "EXISTS(SELECT 1 FROM inserted WHERE CommandKind='Replace')")]
    public async Task FailureAtPersistedBoundaryRollsBackWholeCorrection(string table, string condition)
    {
        // GIVEN a matched unit and a disposable database trigger interrupting a persisted boundary.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await MatchedAsync(context);
        var correction = await CorrectionAsync(context, original, "98");
        await AdminAsync(context, $"CREATE TRIGGER Purchasing.InterruptCorrection ON Purchasing.{table} AFTER INSERT AS BEGIN IF {condition} THROW 51999,'Disposable interruption',1; END");
        // WHEN the correction is interrupted THEN no inverse, replacement, match, period or receipt survives.
        Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(correction.ToJsonString()))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionCorrectionGroups"));
        Assert.Equal(0, await context.CountAsync("RecognitionEventCorrections"));
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(1, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(2, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.Journal.CountAsync("CorrectionReceipts"));
        Assert.Equal(1, await context.Journal.CountAsync("Periods"));
        // AND removing the interruption allows the complete operation to commit.
        await AdminAsync(context, "DROP TRIGGER Purchasing.InterruptCorrection");
        Assert.Equal(4, (await context.CorrectAsync(correction.ToJsonString())).JournalIds.Length);
    }

    [Fact]
    public async Task IndividualRecognitionJournalCannotBeCorrectedThroughGenericKernel()
    {
        // GIVEN a matched recognition journal and a privileged fixture which can invoke the otherwise ungranted kernel.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await MatchedAsync(context);
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await new Workbench.Server.Tenancy.TenantContextProof(context.Journal.ProofKey).ApplyAsync(admin, JournalTestContext.TenantId, default);
        await using var command = new SqlCommand("""
            BEGIN TRY
              BEGIN TRANSACTION;
              DECLARE @journal uniqueidentifier=(SELECT TOP(1) JournalId FROM Purchasing.RecognitionSideEvents WHERE Side='Invoice');
              EXEC Accounting.CorrectJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                @RequiredPermission=N'PurchaseRecognitionFixtureCorrect',@SourceCommandKind=N'PurchaseRecognition.CorrectInternal',
                @SourceCommandVersion=1,@CanonicalInput=N'{}',@OriginalJournalId=@journal,@ExpectedConfigurationVersion=@version,
                @PostingDate='2026-03-01',@Reason=N'Incomplete correction',@Evidence=N'{}';
              COMMIT;
            END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            """, admin);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Journal.SessionId);
        command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@version", context.Journal.ConfigurationVersion);
        // WHEN only one journal is requested THEN SQL requires the discovered source dependency group.
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(51009, error.Number);
        Assert.Contains("dependency", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, await context.Journal.CountAsync("JournalEntries"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedCapacityAndRoundingCannotBeReusedBeforeCorrectionDate(bool nextRevision)
    {
        // GIVEN a fully claimed invoice source with its one permitted rounding component, reversed in March.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var invoice = await context.CommandAsync("Invoice", cost: "100.01");
        var side = invoice["units"]![0]!["sides"]![0]!;
        side["components"]![0]!["amount"] = "100";
        side["components"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { componentKey = "round", kind = "Rounding", amount = "0.01", assignedCostComponentKey = "base", reason = "Reviewed rounding" }));
        await context.PostAsync(invoice.ToJsonString());
        var correction = await CorrectionAsync(context, invoice, null);
        await context.CorrectAsync(correction.ToJsonString());
        if (nextRevision)
        {
            var revised = await CorrectionAsync(context, invoice, "100.01");
            side["sourceRevision"] = revised["replacement"]!["sides"]![0]!["sourceRevision"]!.DeepClone();
        }
        invoice["units"]![0]!["unitId"] = Guid.NewGuid().ToString();
        side["subdivisionKey"] = "replacement-allocation";
        // WHEN a fresh allocation would reuse the released claim before March THEN the temporal source floor rejects it.
        var early = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(invoice.ToJsonString()));
        Assert.Equal(51000, early.Number);
        Assert.Contains("releasing", early.Message, StringComparison.Ordinal);
        // AND allocation on the release date uses active capacity and rounding without counting reversed history.
        invoice["postingDate"] = "2026-03-01";
        Assert.Single((await context.PostAsync(invoice.ToJsonString())).JournalIds);
        Assert.Equal(100.01m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(-100.01m, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
    }

    [Theory]
    [InlineData("2", "90", "1", "50")]
    [InlineData("3", "90", "0.5", "50")]
    [InlineData("2", "100", "1", "0")]
    public async Task SourceCapacityIncludesSurvivingClaimsFromEarlierRevisions(string capacityQuantity, string capacityAmount, string extraQuantity, string extraAmount)
    {
        // GIVEN A and B each claiming 1/50, followed by A2 claiming 1/40 on a new source revision while B survives.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await SharedSourceAsync(context);
        var second = AnotherSubdivision(first, "B", "1", "50");
        await context.PostAsync(first.ToJsonString());
        await context.PostAsync(second.ToJsonString());
        var correction = await CorrectionAsync(context, first, "40");
        var replacement = correction["replacement"]!["sides"]![0]!;
        await SetCapacityAsync(context, replacement, capacityQuantity, capacityAmount);
        await context.CorrectAsync(correction.ToJsonString());
        var extra = first.DeepClone().AsObject();
        extra["units"] = new JsonArray(correction["replacement"]!.DeepClone());
        extra["postingDate"] = "2026-03-01";
        extra = AnotherSubdivision(extra, "C", extraQuantity, extraAmount);
        // WHEN C fits the new revision alone but exceeds the logical source quantity or amount THEN SQL rejects it.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(extra.ToJsonString()));
        Assert.Equal(51009, error.Number);
        Assert.Contains("capacity", error.Message, StringComparison.Ordinal);
        Assert.Equal(90m, await context.BalanceAsync("Inventory"));
        Assert.Equal(3, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(3, await context.CountAsync("RecognitionGroupReceipts"));
    }

    [Fact]
    public async Task ReplacementCannotAddRoundingWhileEarlierRevisionRetainsIt()
    {
        // GIVEN two invoice subdivisions, with the one approved rounding component retained by B on revision 1.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await SharedSourceAsync(context, "Invoice", "100.01");
        var second = AnotherSubdivision(first, "B", "1", "50.01");
        AddRounding(second["units"]![0]!["sides"]![0]!, "50");
        await context.PostAsync(first.ToJsonString());
        await context.PostAsync(second.ToJsonString());
        var correction = await CorrectionAsync(context, first, "40.01");
        var replacement = correction["replacement"]!["sides"]![0]!;
        await SetCapacityAsync(context, replacement, "2", "90.02");
        AddRounding(replacement, "40");
        // WHEN unrounded A is replaced with another rounding component on revision 2 THEN the complete correction rolls back.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(correction.ToJsonString()));
        Assert.Equal(51000, error.Number);
        Assert.Contains("rounding", error.Message, StringComparison.Ordinal);
        Assert.Equal(100.01m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(0, await context.CountAsync("RecognitionCorrectionGroups"));
        Assert.Equal(0, await context.CountAsync("RecognitionEventCorrections"));
        Assert.Equal(0, await context.Journal.CountAsync("CorrectionReceipts"));
    }

    [Fact]
    public async Task SupersededLargerCapacityCannotAuthorizeNewClaimsButOriginalReceiptReplays()
    {
        // GIVEN capacity 3/100 on revision 1, with A corrected to revision 2 capacity 3/90 while B remains posted.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await SharedSourceAsync(context);
        await SetCapacityAsync(context, first["units"]![0]!["sides"]![0]!, "3", "100");
        var request = Guid.NewGuid();
        var originalReceipt = await context.PostAsync(first.ToJsonString(), request);
        await context.PostAsync(AnotherSubdivision(first, "B", "1", "50").ToJsonString());
        var correction = await CorrectionAsync(context, first, "40");
        await SetCapacityAsync(context, correction["replacement"]!["sides"]![0]!, "3", "90");
        await context.CorrectAsync(correction.ToJsonString());
        var stale = AnotherSubdivision(first, "C", "0.1", "5");
        stale["postingDate"] = "2026-03-01";
        // WHEN C fits remaining quantity and the obsolete amount limit THEN the adapter rejects its superseded revision.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(stale.ToJsonString()))).Number);
        Assert.Equal(90m, await context.BalanceAsync("Inventory"));
        // AND an exact authorized retry returns its immutable receipt without revalidating mutable source eligibility.
        var replay = await context.PostAsync(first.ToJsonString(), request);
        Assert.Equal(JsonSerializer.Serialize(originalReceipt), JsonSerializer.Serialize(replay));
        Assert.Equal(3, await context.CountAsync("RecognitionGroupReceipts"));
    }

    [Fact]
    public async Task RevokedSourceCorrectionPermissionPreventsReceiptReplay()
    {
        // GIVEN a successful correction whose source-specific authorization is subsequently revoked.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await context.CommandAsync();
        await context.PostAsync(original.ToJsonString());
        var correction = await CorrectionAsync(context, original, null);
        var request = Guid.NewGuid();
        await context.CorrectAsync(correction.ToJsonString(), request);
        await AdminAsync(context, "UPDATE Purchasing.FixtureRecognitionSources SET CorrectionAllowed=0");
        // WHEN retrying the identical retained receipt THEN live authority fails before private evidence is returned.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(correction.ToJsonString(), request))).Number);
        Assert.Equal(1, await context.CountAsync("RecognitionCorrectionGroups"));
    }

    [Theory]
    [InlineData("reason", "\u00a0\u2003")]
    [InlineData("operation", "Reverse ")]
    public async Task ZeroEvidenceCorrectionStillRequiresStrictOperationAndMeaningfulReason(string property, string value)
    {
        // GIVEN zero evidence, so no journal-kernel validation can accidentally enforce the recognition command contract.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await context.CommandAsync(cost: "0");
        await context.PostAsync(original.ToJsonString());
        var correction = await CorrectionAsync(context, original, null);
        correction[property] = value;
        // WHEN the operation or reason is invalid THEN recognition itself rejects before recording a zero-side correction.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(correction.ToJsonString()))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionCorrectionGroups"));
    }

    internal static async Task<JsonObject> MatchedAsync(PurchaseRecognitionTestContext context, string receiptCost = "100", string invoiceCost = "105")
    {
        var receipt = await context.CommandAsync(cost: receiptCost);
        var invoice = await context.CommandAsync("Invoice", cost: invoiceCost);
        var side = invoice["units"]![0]!["sides"]![0]!;
        side["evidence"]!["varianceAmount"] = (decimal.Parse(invoiceCost, System.Globalization.CultureInfo.InvariantCulture) - decimal.Parse(receiptCost, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        side["evidence"]!["varianceReason"] = "Reviewed final price";
        side["evidence"]!["varianceClassification"] = "Inventory";
        side["evidence"]!["inventoryAdjustmentState"] = "Held";
        await StoreAsync(context, side);
        receipt["units"]![0]!["sides"]!.AsArray().Add(side.DeepClone());
        await context.PostAsync(receipt.ToJsonString());
        return receipt;
    }

    private static async Task<JsonObject> SharedSourceAsync(PurchaseRecognitionTestContext context, string side = "Recognition", string capacity = "100")
    {
        var command = await context.CommandAsync(side, cost: "50");
        var source = command["units"]![0]!["sides"]![0]!;
        source["subdivisionKey"] = "A";
        await SetCapacityAsync(context, source, "2", capacity);
        return command;
    }

    private static JsonObject AnotherSubdivision(JsonObject source, string subdivision, string quantity, string amount)
    {
        var command = source.DeepClone().AsObject();
        var unit = command["units"]![0]!;
        unit["unitId"] = Guid.NewGuid().ToString();
        unit["quantity"] = quantity;
        var side = unit["sides"]![0]!;
        side["eventRevision"] = 1;
        side["subdivisionKey"] = subdivision;
        side["sourceQuantity"] = quantity;
        side["sourceAmount"] = amount;
        side["components"]![0]!["amount"] = amount;
        return command;
    }

    private static Task SetCapacityAsync(PurchaseRecognitionTestContext context, JsonNode side, string quantity, string amount)
    {
        side["evidence"]!["sourceCapacityQuantity"] = quantity;
        side["evidence"]!["sourceCapacityAmount"] = amount;
        return StoreAsync(context, side);
    }

    private static void AddRounding(JsonNode side, string baseAmount)
    {
        side["components"]![0]!["amount"] = baseAmount;
        side["components"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { componentKey = "round", kind = "Rounding", amount = "0.01", assignedCostComponentKey = "base", reason = "Approved source rounding" }));
    }

    internal static async Task<JsonObject> CorrectionAsync(PurchaseRecognitionTestContext context, JsonObject original, string? cost, string classification = "Inventory")
    {
        var unit = original["units"]![0]!;
        JsonObject? replacement = null;
        if (cost is not null)
        {
            replacement = unit.DeepClone().AsObject();
            replacement["unitId"] = Guid.NewGuid().ToString();
            replacement["classification"] = classification;
            replacement["expectedPriorEventRevision"] = 0;
            replacement["sides"] = new JsonArray();
            foreach (var old in unit["sides"]!.AsArray())
            {
                var source = await context.CommandAsync(old!["side"]!.GetValue<string>(), classification, cost);
                var side = source["units"]![0]!["sides"]![0]!.DeepClone();
                var generatedId = side["sourceId"]!.GetValue<string>();
                side["sourceId"] = old["sourceId"]!.DeepClone();
                side["subdivisionKey"] = old["subdivisionKey"]!.DeepClone();
                side["eventRevision"] = old["eventRevision"]!.GetValue<int>() + 1;
                side["effectiveDate"] = old["effectiveDate"]!.DeepClone();
                await context.PublishSourceRevisionAsync(side, generatedId);
                replacement["sides"]!.AsArray().Add(side);
            }
        }
        return JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            operation = cost is null ? "Reverse" : "Replace",
            expectedConfigurationVersion = context.Journal.ConfigurationVersion,
            purchaseOrderId = context.PurchaseOrderId,
            expectedPurchaseOrderVersion = context.PurchaseOrderVersion,
            unitId = unit["unitId"]!.GetValue<string>(),
            expectedEventRevisions = unit["sides"]!.AsArray().Select(s => new { side = s!["side"]!.GetValue<string>(), eventRevision = s["eventRevision"]!.GetValue<int>() }),
            postingDate = "2026-03-01",
            reason = "Reviewed source correction",
            replacement
        })!.AsObject();
    }

    internal static Task StoreAsync(PurchaseRecognitionTestContext context, JsonNode side)
        => AdminAsync(context, "UPDATE Purchasing.FixtureRecognitionSources SET EvidenceJson=@evidence WHERE Id=@id AND Revision=@revision", ("@evidence", side["evidence"]!.ToJsonString()), ("@id", side["sourceId"]!.GetValue<string>()), ("@revision", side["sourceRevision"]!.GetValue<string>()));

    internal static async Task AdminAsync(PurchaseRecognitionTestContext context, string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(sql, admin);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<T> ScalarAsync<T>(PurchaseRecognitionTestContext context, string sql)
    {
        await using var command = new SqlCommand(sql, context.Connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<string> SnapshotAsync(PurchaseRecognitionTestContext context, string where = "")
        => ScalarAsync<string>(context, $"SELECT * FROM Purchasing.RecognitionSideEvents e {where} ORDER BY Id FOR JSON PATH");

    private static async Task AssertGroupTimeAsync(PurchaseRecognitionTestContext context, RecognitionResult result)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM (
              SELECT RecordedAtUtc FROM Purchasing.RecognitionCorrectionGroups
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.RecognitionSideEvents WHERE CorrectionGroupId IS NOT NULL
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.RecognitionMatches WHERE CorrectionGroupId IS NOT NULL
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.RecognitionGroupReceipts WHERE CommandKind<>'Post'
              UNION ALL SELECT RecordedAtUtc FROM Accounting.JournalEntries WHERE PostingDate='2026-03-01'
              UNION ALL SELECT RecordedAtUtc FROM Accounting.SourceEvents WHERE PostingDate='2026-03-01'
              UNION ALL SELECT RecordedAtUtc FROM Accounting.CorrectionGroups
              UNION ALL SELECT p.RecordedAtUtc FROM Accounting.PostingReceipts p JOIN Accounting.JournalEntries j ON j.Id=p.JournalId WHERE j.PostingDate='2026-03-01'
              UNION ALL SELECT OccurredAtUtc FROM Security.TenantSecurityAuditEvents WHERE Action IN ('Purchasing.CorrectRecognition','Accounting.CorrectJournal')
            ) t WHERE RecordedAtUtc<>@instant
            """, context.Connection);
        command.Parameters.AddWithValue("@instant", result.RecordedAtUtc);
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }
}
