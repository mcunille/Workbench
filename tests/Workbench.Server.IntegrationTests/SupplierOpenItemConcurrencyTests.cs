// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentAllocationsCannotOverspend(bool firstTargetWins, bool sharedDebt)
    {
        // GIVEN real deposits and bills competing for either one funding item or one debt item.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var secondFunding = funding;
        if (sharedDebt)
        {
            var other = await context.CommandAsync(); await context.RecordAsync(other);
            secondFunding = Guid.Parse(other["paymentId"]!.ToString());
        }
        var firstBill = await context.Allocation.BillAsync("100"); var secondBill = sharedDebt ? firstBill : await context.Allocation.BillAsync("100");
        var first = await context.Allocation.CommandAsync(funding, firstBill);
        var second = await context.Allocation.CommandAsync(secondFunding, secondBill);
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        var successful = 0;
        async Task Apply(JsonObject command, SqlConnection connection)
        { await context.Bills.ExecuteAsync("ApplySupplierFunds", Guid.NewGuid(), command, connection); successful++; }
        // WHEN one contender holds the transaction while its peer demonstrably blocks.
        var error = await OrderedAsync(context, firstTargetWins, sibling,
            () => Apply(first, context.Allocation.Journal.Connection), () => Apply(second, sibling));
        // THEN exactly one application consumes the contested capacity; the losing source remains open.
        Assert.Equal(51009, error?.Number); Assert.Equal(1, successful);
        Assert.Equal(!sharedDebt || firstTargetWins ? 0m : 100m, await context.Allocation.BalanceAsync(funding));
        Assert.Equal(!sharedDebt || !firstTargetWins ? 0m : 100m, await context.Allocation.BalanceAsync(secondFunding));
        Assert.Equal(sharedDebt || firstTargetWins ? 0m : 100m, await context.Allocation.BalanceAsync(firstBill));
        Assert.Equal(sharedDebt || !firstTargetWins ? 0m : 100m, await context.Allocation.BalanceAsync(secondBill));
        Assert.Equal(0m, await MinimumHistoricalBalanceAsync(context));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
    }

    [Theory]
    [InlineData(true)]
    public async Task ConcurrentPaymentsCannotOverSettleBill(bool firstPaymentWins)
    {
        // GIVEN two independently valid payments each attempting to settle the same whole bill.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100");
        var first = await context.CommandAsync(); var second = await context.CommandAsync();
        await context.AllocateAsync(first, bill, "100"); await context.AllocateAsync(second, bill, "100");
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        var successful = 0;
        async Task Pay(JsonObject command, SqlConnection connection)
        { await context.Bills.ExecuteAsync("RecordSupplierPayment", Guid.NewGuid(), command, connection); successful++; }
        // WHEN accounting ownership is forced on separate restricted SQL connections.
        var error = await OrderedAsync(context, firstPaymentWins, sibling,
            () => Pay(first, context.Allocation.Journal.Connection), () => Pay(second, sibling));
        // THEN stale debt cannot admit another payment, journal, application or receipt.
        Assert.Equal(51009, error?.Number); Assert.Equal(1, successful);
        Assert.Equal(firstPaymentWins ? first["paymentId"]!.ToString() : second["paymentId"]!.ToString(),
            (await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Purchasing.SupplierPayments")).ToString());
        Assert.Equal(0m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(0m, await MinimumHistoricalBalanceAsync(context));
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseAndAllocationHaveOnlyValidSerialOutcomes(bool allocationFirst)
    {
        // GIVEN real funding and debt with an open September posting month.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var bill = await context.Allocation.BillAsync();
        var input = await context.Allocation.CommandAsync(Guid.Parse(payment["paymentId"]!.ToString()), bill);
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        async Task Close()
        {
            await using var command = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-09-01',@Reason=N'Reconciled',@Evidence=NULL", sibling);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
            command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@version", context.Allocation.Journal.ConfigurationVersion);
            await command.ExecuteNonQueryAsync();
        }
        // WHEN close and allocation overlap in each forced order THEN no new posting follows a completed close.
        var error = await OrderedAsync(context, allocationFirst, sibling, () => context.Allocation.ApplyAsync(input), Close);
        if (allocationFirst) Assert.Null(error); else Assert.Equal(51009, error?.Number);
        Assert.Equal(allocationFirst ? 50m : 150m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(allocationFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(1, await context.Allocation.Journal.CountAsync("PeriodClosures"));
    }

    [Theory]
    [InlineData("mapping", true)]
    [InlineData("mapping", false)]
    [InlineData("archive", true)]
    [InlineData("archive", false)]
    public async Task PaymentRechecksConfigurationAndFundingUnderTheCommonLock(string change, bool paymentFirst)
    {
        // GIVEN a payment prepared against current mappings and an active Bank account.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var payment = await context.CommandAsync();
        var payload = JsonNode.Parse(await context.Bills.ScalarAsync<string>("SELECT Payload FROM Accounting.Configurations"))!;
        var replacement = await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """[{"code":"ADV5","name":"New advances","type":"Asset","purpose":"SupplierAdvance"}]""");
        payload["mappings"]!.AsArray().Single(x => x!["slot"]!.ToString() == "SupplierAdvance")!["accountId"] = JsonNode.Parse(replacement.Ids)![0]!.DeepClone();
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        Task Change() => change == "mapping"
            ? context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "Configure", payload.ToJsonString(), expectedVersion: context.Allocation.Journal.ConfigurationVersion, connection: sibling)
            : context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""", context.Bank, Guid.Parse(payment["expectedFundingAccountVersion"]!.ToString()), sibling);
        // WHEN the real configuration/archive command and payment overlap in both serial orders.
        var error = await OrderedAsync(context, paymentFirst, sibling, () => context.RecordAsync(payment), Change);
        // THEN a stale configuration or archived funding rejects the new payment; completed cash evidence remains immutable.
        if (!paymentFirst) Assert.Equal(change == "mapping" ? 51009 : 51004, error?.Number);
        else if (change == "mapping") Assert.Null(error);
        else Assert.Equal(50909, error?.Number);
        Assert.Equal(paymentFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        if (paymentFirst) Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupplierAmendmentAndFirstPaymentRespectBothSerialOrders(bool paymentFirst)
    {
        // GIVEN an ordered PO without financial history and a real supplier-removal amendment.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("UPDATE Purchasing.DraftOrders SET PoNumber=1");
        context.Bills.Recognition.PurchaseOrderVersion = await context.Bills.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.DraftOrders");
        var payment = await context.CommandAsync();
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        async Task Amend()
        {
            var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierId = null, SupplierName = "Replacement supplier", Notes = "Amendment", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
            await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", sibling) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", JournalTestContext.ActorId);
            command.Parameters.AddWithValue("@TargetId", context.Bills.Recognition.PurchaseOrderId); command.Parameters.AddWithValue("@ExpectedVersion", Convert.FromHexString(context.Bills.Recognition.PurchaseOrderVersion[2..]));
            command.Parameters.AddWithValue("@Operation", "Amend"); command.Parameters.AddWithValue("@OrderDate", "2026-01-01"); command.Parameters.AddWithValue("@Reason", "Amendment");
            command.Parameters.AddWithValue("@Draft", JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions)); command.Parameters.AddWithValue("@Calculation", "{}");
            await command.ExecuteNonQueryAsync();
        }
        // WHEN each source operation holds the shared lock first THEN only its compatible outcome commits.
        var error = await OrderedAsync(context, paymentFirst, sibling, () => context.RecordAsync(payment), Amend);
        Assert.Equal(paymentFirst ? 50415 : 51009, error?.Number);
        Assert.Equal(paymentFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(paymentFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE SupplierId IS NOT NULL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentRemovalAndPaymentRespectBothSerialOrders(bool paymentFirst)
    {
        // GIVEN an available PO document referenced by a complete payment envelope.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var (document, revision) = await context.Bills.SeedDocumentAsync();
        var version = await context.Bills.ScalarAsync<byte[]>($"SELECT RowVersion FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}'");
        var payment = await context.CommandAsync();
        payment["evidence"] = new JsonObject { ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() }), ["missingEvidenceReason"] = null };
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        async Task Remove()
        {
            await using var command = new SqlCommand("""
                BEGIN TRAN;
                EXEC Purchasing.PreparePurchaseOrderDocument @OrderId=@po,@RequestId=@request,@ExpectedOrderVersion=@poVersion,
                  @Kind=2,@DocumentId=@document,@ExpectedDocumentVersion=@documentVersion,@ActorUserId=@actor;
                EXEC Purchasing.FinishPurchaseOrderDocument @RequestId=@request,@Published=0;
                COMMIT;
                """, sibling);
            command.Parameters.AddWithValue("@po", context.Bills.Recognition.PurchaseOrderId); command.Parameters.AddWithValue("@request", Guid.NewGuid());
            command.Parameters.AddWithValue("@poVersion", Convert.FromHexString(context.Bills.Recognition.PurchaseOrderVersion[2..]));
            command.Parameters.AddWithValue("@document", document); command.Parameters.AddWithValue("@documentVersion", version); command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            await command.ExecuteNonQueryAsync();
        }
        // WHEN actual PO/document locks force each serial order THEN removed evidence cannot support a new payment.
        var error = await RowOrderedAsync(context, paymentFirst, sibling, () => context.RecordAsync(payment), Remove);
        if (paymentFirst) Assert.Null(error); else Assert.Equal(51009, error?.Number);
        Assert.Equal(paymentFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
        if (paymentFirst) Assert.Equal(new string('A', 64), await context.Bills.ScalarAsync<string>("SELECT JSON_VALUE(EvidenceJson,'$.evidence.documents[0].digest') FROM Purchasing.SupplierPayments"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RoleRevocationAndReplayRespectBothSerialOrders(bool application, bool replayFirst)
    {
        // GIVEN actual immutable command evidence and the restricted production role-removal command.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync();
        if (application)
        {
            await context.RecordAsync(input);
            input = await context.Allocation.CommandAsync(Guid.Parse(input["paymentId"]!.ToString()), await context.Allocation.BillAsync());
        }
        var operation = application ? "ApplySupplierFunds" : "RecordSupplierPayment";
        var request = Guid.NewGuid(); var original = await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, request, input);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context, includeSecurityAudit: false);
        string? replay = null;
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        async Task Revoke()
        {
            await using var revoke = new SqlCommand("EXEC Administration.AssignAccountingRoles @ActorId=@actor,@SessionId=@session,@UserId=@actor,@RequestId=@request,@ExpectedVersion=@version,@RoleIds=N'[]'", sibling);
            revoke.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); revoke.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
            revoke.Parameters.AddWithValue("@request", Guid.NewGuid()); revoke.Parameters.AddWithValue("@version", Guid.Empty);
            await revoke.ExecuteNonQueryAsync();
        }
        async Task Replay() => replay = await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, request, input);
        // WHEN HOLDLOCK authority rows force the revoked-first and admitted-first serial outcomes.
        var error = await RowOrderedAsync(context, replayFirst, sibling, Replay, Revoke);
        if (replayFirst) { Assert.Null(error); Assert.Equal(original, replay); }
        else { Assert.Equal(51003, error?.Number); Assert.Null(replay); }
        // THEN all durable financial bytes remain unchanged and every subsequent replay is denied.
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context, includeSecurityAudit: false));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Administration.AccountingRoleReceipts"));
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => Replay())).Number);
    }

    [Theory]
    [InlineData(false, "exact", true)]
    [InlineData(false, "changed", true)]
    [InlineData(false, "changed", false)]
    [InlineData(false, "newRequest", true)]
    [InlineData(true, "exact", true)]
    [InlineData(true, "changed", true)]
    [InlineData(true, "changed", false)]
    [InlineData(true, "newRequest", true)]
    public async Task ConcurrentRetriesPreserveSourceAndReceiptIdentity(bool application, string retryKind, bool originalFirst)
    {
        // GIVEN two submissions of one payment source or one versioned allocation on separate restricted connections.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var original = await context.CommandAsync();
        var funding = Guid.Parse(original["paymentId"]!.ToString());
        if (application)
        {
            await context.RecordAsync(original);
            original = await context.Allocation.CommandAsync(funding, await context.Allocation.BillAsync());
        }
        var operation = application ? "ApplySupplierFunds" : "RecordSupplierPayment";
        var retry = original.DeepClone().AsObject();
        if (retryKind == "changed")
        {
            if (application) retry["targets"]![0]!["amount"] = "50";
            else retry["reference"] = "Different transfer reference";
        }
        var request = Guid.NewGuid(); var retryRequest = retryKind == "newRequest" ? Guid.NewGuid() : request;
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        string? first = null, second = null;
        async Task Original() => first = await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, request, original);
        async Task Retry() => second = await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, retryRequest, retry, sibling);
        // WHEN each submission obtains the transaction lock first THEN only exact replay shares the original receipt.
        var error = await OrderedAsync(context, originalFirst, sibling, Original, Retry);
        if (retryKind == "exact") { Assert.Null(error); Assert.Equal(first, second); }
        else { Assert.Equal(51009, error?.Number); Assert.True((first is null) != (second is null)); }
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(application ? 2 : 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
        Assert.Equal(application ? 3 : 1, await context.Allocation.Journal.CountAsync("JournalEntries"));
        Assert.Equal(application ? (retryKind == "changed" && !originalFirst ? 50m : 0m) : 100m, await context.Allocation.BalanceAsync(funding));
    }

    private static Task<SqlException?> OrderedAsync(SupplierPaymentTestContext context, bool commandFirst, SqlConnection sibling, Func<Task> command, Func<Task> change)
        => PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            commandFirst ? context.Allocation.Journal.Connection : sibling, commandFirst ? sibling : context.Allocation.Journal.Connection,
            commandFirst ? command : change, commandFirst ? change : command);

    private static async Task<SqlException?> RowOrderedAsync(SupplierPaymentTestContext context, bool commandFirst, SqlConnection sibling, Func<Task> command, Func<Task> change)
    {
        var holder = commandFirst ? context.Allocation.Journal.Connection : sibling;
        var waiter = commandFirst ? sibling : context.Allocation.Journal.Connection;
        await ExecuteAsync(holder, "BEGIN TRAN");
        try
        {
            await (commandFirst ? command() : change());
            var pending = CaptureAsync(commandFirst ? change : command);
            await using var observer = new SqlConnection(context.Allocation.Journal.Application.AdminConnectionString); await observer.OpenAsync();
            await using var probe = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@waiter AND blocking_session_id=@holder AND wait_type LIKE 'LCK_M_%' AND (wait_resource LIKE 'KEY:%' OR wait_resource LIKE 'PAGE:%' OR wait_resource LIKE 'OBJECT:%')", observer);
            probe.Parameters.AddWithValue("@waiter", waiter.ServerProcessId); probe.Parameters.AddWithValue("@holder", holder.ServerProcessId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            while ((int)(await probe.ExecuteScalarAsync(timeout.Token))! != 1) await Task.Yield();
            await ExecuteAsync(holder, "COMMIT");
            return await pending;
        }
        finally { await ExecuteAsync(holder, "IF @@TRANCOUNT>0 ROLLBACK"); }
    }

    private static async Task<SqlException?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (SqlException error) { return error; } }
    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    { await using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private static Task<decimal> MinimumHistoricalBalanceAsync(SupplierPaymentTestContext context) => context.Bills.ScalarAsync<decimal>("""
        SELECT MIN(Balance) FROM(SELECT SUM(SUM(Amount)) OVER(PARTITION BY ItemId ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) Balance
          FROM Purchasing.SupplierItemMovements GROUP BY ItemId,PostingDate) boundaries
        """);
}
