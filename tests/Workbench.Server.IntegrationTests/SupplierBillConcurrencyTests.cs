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

    [Theory]
    [InlineData("SupplierBillsManage")]
    [InlineData("SupplierBillsPost")]
    public async Task PostRetryRechecksBothPermissionsBeforeReturningReceipt(string permission)
    {
        // GIVEN a posted invoice and its exact successful request.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context); var input = context.PostCommand(reviewed); var request = Guid.NewGuid();
        var posted = await context.ExecuteAsync("PostSupplierBill", request, input);
        // WHEN mutable purchase state changes THEN exact retry still returns its original receipt.
        await context.AdminAsync("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=SYSUTCDATETIME()");
        Assert.Equal(posted.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, input)).ToJsonString());
        // AND losing either business permission denies that same replay without changing the posting.
        await context.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimValue=N'{permission}'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", request, input))).Number);
        Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DuplicateCreationAndPeriodCloseRespectBothSerialOrders(bool postingFirst, bool closing)
    {
        // GIVEN a reviewed source and an independent writer changing a posting prerequisite.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        await using var sibling = await context.Journal.OpenSiblingAsync();
        if (closing) await context.AdminAsync(JournalControlAdapterSql.Install);
        Task Post() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        async Task Change()
        {
            if (!closing) { await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft(), sibling); return; }
            await using var command = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-02-01',@Reason=N'Reconciled',@Evidence=NULL", sibling);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Journal.SessionId);
            command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@version", context.Journal.ConfigurationVersion);
            await command.ExecuteNonQueryAsync();
        }
        // WHEN real transactions overlap THEN the second writer observes the first committed result.
        var error = await PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Recognition,
            postingFirst ? context.Journal.Connection : sibling, postingFirst ? sibling : context.Journal.Connection,
            postingFirst ? Post : Change, postingFirst ? Change : Post);
        if (postingFirst) Assert.Null(error); else Assert.Equal(51009, error?.Number);
        Assert.Equal(postingFirst ? 1 : 0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(postingFirst ? -110m : 0m, await context.Recognition.BalanceAsync("SupplierPayable"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentRemovalAndPostingRespectBothSerialOrders(bool postingFirst)
    {
        // GIVEN a reviewed document and the existing restricted document removal command.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var (document, revision) = await context.SeedDocumentAsync();
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() });
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var documentVersion = await context.ScalarAsync<byte[]>($"SELECT RowVersion FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}'");
        await using var sibling = await context.Journal.OpenSiblingAsync();
        Task Post() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        async Task Remove()
        {
            await using var command = new SqlCommand("""
                BEGIN TRANSACTION;
                EXEC Purchasing.PreparePurchaseOrderDocument @OrderId=@po,@RequestId=@request,@ExpectedOrderVersion=@poVersion,
                  @Kind=2,@DocumentId=@document,@ExpectedDocumentVersion=@documentVersion,@ActorUserId=@actor;
                EXEC Purchasing.FinishPurchaseOrderDocument @RequestId=@request,@Published=0;
                COMMIT;
                """, sibling);
            command.Parameters.AddWithValue("@po", context.Recognition.PurchaseOrderId); command.Parameters.AddWithValue("@request", Guid.NewGuid());
            command.Parameters.AddWithValue("@poVersion", Convert.FromHexString(context.Recognition.PurchaseOrderVersion[2..]));
            command.Parameters.AddWithValue("@document", document); command.Parameters.AddWithValue("@documentVersion", documentVersion);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); await command.ExecuteNonQueryAsync();
        }
        var first = postingFirst ? context.Journal.Connection : sibling;
        var second = postingFirst ? sibling : context.Journal.Connection;
        await using var transaction = new SqlCommand("BEGIN TRANSACTION", first); await transaction.ExecuteNonQueryAsync();
        try
        {
            // WHEN one source transaction retains its PO/document locks while the other arrives.
            await (postingFirst ? Post() : Remove());
            var competing = Record.ExceptionAsync(postingFirst ? Remove : Post);
            await WaitForRowLockAsync(context, first.ServerProcessId, second.ServerProcessId);
            transaction.CommandText = "COMMIT"; await transaction.ExecuteNonQueryAsync();
            var error = await competing;
            // THEN removal prevents later posting, while a first posting denies removal and retains its exact evidence.
            Assert.Equal(postingFirst ? 51011 : 51009, Assert.IsType<SqlException>(error).Number);
            Assert.Equal(postingFirst ? 1 : 0, await context.Journal.CountAsync("JournalEntries"));
            Assert.Equal(postingFirst ? 0 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
            Assert.Equal(postingFirst ? 1 : 0, await context.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks WHERE DocumentId='{document}' AND RevisionId='{revision}' AND Sha256=REPLICATE('A',64)"));
            if (postingFirst) Assert.Equal(documentVersion, await context.ScalarAsync<byte[]>($"SELECT RowVersion FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}'"));
        }
        finally { transaction.CommandText = "IF @@TRANCOUNT>0 ROLLBACK"; await transaction.ExecuteNonQueryAsync(); }
    }

    private static async Task WaitForRowLockAsync(SupplierBillTestContext context, int holder, int waiter)
    {
        // Document commands coordinate on the PO row, not the accounting application lock.
        await using var observer = new SqlConnection(context.Journal.Application.AdminConnectionString); await observer.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@waiter AND blocking_session_id=@holder AND wait_type LIKE 'LCK_M_%' AND wait_resource LIKE 'KEY:%'", observer);
        command.Parameters.AddWithValue("@holder", holder); command.Parameters.AddWithValue("@waiter", waiter);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while ((int)(await command.ExecuteScalarAsync(timeout.Token))! != 1) await Task.Delay(30, timeout.Token);
    }

    private static async Task<(JsonObject? Result, SqlException? Error)> CaptureAsync(Task<JsonObject> task)
    { try { return (await task, null); } catch (SqlException error) { return (null, error); } }
}
