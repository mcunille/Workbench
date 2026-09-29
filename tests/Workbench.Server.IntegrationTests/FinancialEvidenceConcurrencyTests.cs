// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceConcurrencyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task EvidenceReadWaitsForAppendBeforeHoldingSourceOrPurchaseLocks()
    {
        // GIVEN a source writer holding its established Accounting then PO coordination locks.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var original = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, original);
        var additional = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var owner = await context.ScalarAsync<Guid>("SELECT OwnerId FROM Accounting.FinancialEvidenceSets");
        var revision = await context.ScalarAsync<Guid>("SELECT OwnerRevisionId FROM Accounting.FinancialEvidenceSets");
        await FinancialEvidenceSecurityTests.InstallAdapterAsync(context);
        var append = await FinancialEvidenceSecurityTests.CommandAsync(context, owner, additional);
        await using var database = BlobPersistenceTests.CreateContext(context.Journal.Application.WebConnectionString,
            new TenantContextProof(context.Journal.ProofKey), JournalTestContext.TenantId);
        await using var readTransaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var readerConnection = (SqlConnection)database.Database.GetDbConnection();
        var actor = new Workbench.Server.Authorization.RequestActor(JournalTestContext.ActorId, JournalTestContext.TenantId,
            context.Journal.SessionId, new HashSet<string> { "AccountingReportsRead" });
        await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, $"""
            BEGIN TRAN;
            DECLARE @tenant uniqueidentifier='{JournalTestContext.TenantId}',@result int;
            DECLARE @resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
            IF @result<0 THROW 51010,'Test writer could not acquire Accounting coordination.',1;
            SELECT RowVersion FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE Id='{context.Recognition.PurchaseOrderId}';
            """);
        Task<Workbench.Server.Accounting.FinancialEvidenceSetResponse?> read;
        try
        {
            // WHEN a serializable source read competes with the real append command.
            read = Workbench.Server.Persistence.FinancialEvidenceQueries.ReadAsync(database, actor, owner, revision, default);
            await WaitForBlockedAsync(context, context.Journal.Connection, readerConnection);
            await FinancialEvidenceSecurityTests.AppendAsync(context, append, Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "IF @@TRANCOUNT>0 COMMIT;"); }
        // THEN append cannot deadlock on an evidence-set lock taken by the reader before Accounting coordination.
        Assert.Equal(2, (await read.WaitAsync(TimeSpan.FromSeconds(15)))!.Links.Length);
        await readTransaction.CommitAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalAndNewSourceLinkSerializeCompleteMembership(bool linkWins)
    {
        // GIVEN expired evidence and a second authentic metadata-only source that may acquire this document.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await FinancialEvidenceDisposalTests.ExpireAsync(context);
        var second = context.CompleteDraft("INV-SECOND"); second["revision"]!["total"] = "0";
        foreach (var component in second["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, second);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var missing = JsonNode.Parse(await FinancialEvidenceDisposalTests.ExecuteAsync(context,
            $"EXEC Purchasing.ReadSupplierBill '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{posted["billId"]}'"))!["financialEvidence"]!;
        Assert.Empty(missing["links"]!.AsArray());
        Assert.Equal("Reviewed invoice supplied without a file", missing["missingEvidenceReason"]!.ToString());
        await FinancialEvidenceSecurityTests.InstallAdapterAsync(context);
        var append = await FinancialEvidenceSecurityTests.CommandAsync(context, Guid.Parse(posted["billId"]!.GetValue<string>()), document);
        var appendSql = $"EXEC Accounting.AppendFixtureFinancialEvidence '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{Guid.NewGuid()}',N'{append.ToJsonString().Replace("'", "''", StringComparison.Ordinal)}'";
        var disposal = await FinancialEvidenceDisposalTests.CommandAsync(context, document.Document, Guid.NewGuid());
        await using var contender = await context.Journal.OpenSiblingAsync();
        await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "BEGIN TRAN;");
        Task<Exception?> losing;
        try
        {
            await FinancialEvidenceDisposalTests.ExecuteAsync(context, linkWins ? appendSql : disposal);
            // WHEN the second actual command waits on the winner's transaction.
            losing = Record.ExceptionAsync(() => FinancialEvidenceDisposalTests.ExecuteAsync(context, linkWins ? disposal : appendSql, contender));
            await WaitForBlockedAsync(context, context.Journal.Connection, contender);
        }
        finally { await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "COMMIT;"); }
        // THEN new membership invalidates disposal, or removal prevents later acquisition.
        Assert.Equal(linkWins ? 51009 : 51004, Assert.IsType<SqlException>(await losing.WaitAsync(TimeSpan.FromSeconds(15))).Number);
        Assert.Equal(linkWins ? 2 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(linkWins ? 0 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceDisposals"));
        if (linkWins)
        {
            // AND refreshing the token does not waive the newly acquired indefinite deadline.
            var refreshed = await FinancialEvidenceDisposalTests.CommandAsync(context, document.Document, Guid.NewGuid());
            Assert.Equal(51011, (await Assert.ThrowsAsync<SqlException>(() => FinancialEvidenceDisposalTests.ExecuteAsync(context, refreshed))).Number);
            await FinancialEvidenceDisposalTests.ExpireAsync(context);
            await FinancialEvidenceDisposalTests.ExecuteAsync(context, refreshed);
            Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceDisposalLinks"));
        }
    }

    [Fact]
    public async Task IndependentHoldWinningAttachmentLockBlocksDisposal()
    {
        // GIVEN eligible retained evidence and a separately authorized independent hold transaction.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await FinancialEvidenceDisposalTests.ExpireAsync(context);
        var sql = await FinancialEvidenceDisposalTests.CommandAsync(context, document.Document, Guid.NewGuid());
        await using var holder = new SqlConnection(context.Journal.Application.AdminConnectionString); await holder.OpenAsync();
        await using (var begin = new SqlCommand("BEGIN TRAN; UPDATE Storage.Attachments SET Held=1,IndependentHeld=1;", holder)) await begin.ExecuteNonQueryAsync();
        Task<Exception?> losing;
        try
        {
            // WHEN disposal reaches the held attachment after its accounting and PO coordination.
            losing = Record.ExceptionAsync(() => FinancialEvidenceDisposalTests.ExecuteAsync(context, sql));
            await WaitForBlockedAsync(context, holder, context.Journal.Connection);
        }
        finally { await using var commit = new SqlCommand("COMMIT;", holder); await commit.ExecuteNonQueryAsync(); }
        // THEN it sees the committed hold and preserves both document and bytes.
        Assert.Equal(51011, Assert.IsType<SqlException>(await losing.WaitAsync(TimeSpan.FromSeconds(15))).Number);
        await FinancialEvidenceRemovalTests.AssertPreservedAsync(context, storage, document.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostingAndRemovalHaveOneLegalWinner(bool postingWins)
    {
        // GIVEN a reviewed bill with live uploaded evidence and independently open SQL sessions.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var financialBefore = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        await using var contender = await context.Journal.OpenSiblingAsync();
        Assert.NotEqual(context.Journal.Connection.ServerProcessId, contender.ServerProcessId);
        var request = Guid.NewGuid();
        await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "BEGIN TRAN;");
        Task<Exception?> losing;
        try
        {
            if (postingWins)
            {
                await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
                losing = Record.ExceptionAsync(async () =>
                {
                    await using var remove = new SqlCommand(FinancialEvidenceRemovalTests.PrepareSql(context, document.Document, request), contender);
                    await remove.ExecuteNonQueryAsync();
                });
            }
            else
            {
                await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, FinancialEvidenceRemovalTests.PrepareSql(context, document.Document, request));
                await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, $"EXEC Purchasing.FinishPurchaseOrderDocument '{request}',0;");
                losing = Record.ExceptionAsync(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed), contender));
            }
            // WHEN the competing operation demonstrably waits on the first transaction's SQL lock.
            await WaitForBlockedAsync(context, context.Journal.Connection, contender);
        }
        finally { await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "COMMIT;"); }
        // THEN releasing the winner produces a domain conflict, never a deadlock or partial posting.
        var error = Assert.IsType<SqlException>(await losing.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(postingWins ? 51011 : 51009, error.Number);
        Assert.Equal(postingWins ? 1 : 0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(postingWins ? 1 : 0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillPostings"));
        if (postingWins) await FinancialEvidenceRemovalTests.AssertPreservedAsync(context, storage, document.Revision);
        else
        {
            Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
            Assert.Equal(financialBefore, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        }
    }

    [Fact]
    public async Task CleanupOwningAttachmentCannotDeadlockCaptureOrDeleteActiveEvidence()
    {
        // GIVEN a stale cleanup lease whose worker has already locked work then attachment.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var work = Guid.NewGuid(); var owner = Guid.NewGuid();
        await context.AdminAsync($"""
            INSERT Operations.WorkItems(Id,TenantId,Kind,AttachmentId,State,Attempts,Generation,CreatedAtUtc,AvailableAtUtc,LeaseOwner,LeaseExpiresAtUtc)
            SELECT '{work}',TenantId,1,Id,1,1,2,SYSUTCDATETIME(),SYSUTCDATETIME(),'{owner}',DATEADD(hour,1,SYSUTCDATETIME()) FROM Storage.Attachments;
            """);
        await using var cleanup = new SqlConnection(await context.Journal.Application.CreateWorkerConnectionAsync());
        await cleanup.OpenAsync(); await new TenantContextProof(context.Journal.ProofKey).ApplyAsync(cleanup, JournalTestContext.TenantId, default);
        await using (var begin = new SqlCommand("SET XACT_ABORT OFF; BEGIN TRAN;", cleanup)) await begin.ExecuteNonQueryAsync();
        await using (var admission = new SqlCommand($"EXEC Operations.LockWork '{work}','{owner}',2", cleanup))
            Assert.Equal(51011, (await Assert.ThrowsAsync<SqlException>(() => admission.ExecuteScalarAsync())).Number);
        await using (var transaction = new SqlCommand("SELECT @@TRANCOUNT;", cleanup))
            Assert.Equal(1, (int)(await transaction.ExecuteScalarAsync())!);
        var posting = context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        try
        {
            // WHEN posting holds the PO/accounting coordination and waits on the worker's attachment.
            await WaitForBlockedAsync(context, cleanup, context.Journal.Connection);
            await using var guard = new SqlCommand($"EXEC Operations.LockWork '{work}','{owner}',2", cleanup);
            // THEN cleanup rejects the active attachment without trying to acquire that PO/accounting lock.
            Assert.Equal(51011, (await Assert.ThrowsAsync<SqlException>(() => guard.ExecuteScalarAsync())).Number);
        }
        finally
        {
            await using var release = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK;", cleanup); await release.ExecuteNonQueryAsync();
        }
        await posting.WaitAsync(TimeSpan.FromSeconds(15));
        // AND an older generation cannot complete the work; the current generation cannot purge the new evidence either.
        await using var stale = new SqlCommand($"EXEC Operations.CompleteWork '{work}','{owner}',1", cleanup);
        Assert.Equal(0, (int)(await stale.ExecuteScalarAsync())!);
        await using var current = new SqlCommand($"EXEC Operations.CompleteWork '{work}','{owner}',2", cleanup);
        Assert.Equal(51011, (await Assert.ThrowsAsync<SqlException>(() => current.ExecuteScalarAsync())).Number);
        await using (var rollback = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK;", cleanup)) await rollback.ExecuteNonQueryAsync();
        await FinancialEvidenceRemovalTests.AssertPreservedAsync(context, storage, document.Revision);
    }

    [Fact]
    public async Task OppositeDocumentInputOrderPreservesBothSourcesWithoutDeadlock()
    {
        // GIVEN two reviewed sources declaring the same two uploaded documents in opposite order.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var firstDocument = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var secondDocument = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var firstDraft = context.CompleteDraft("ORDER-A"); var secondDraft = context.CompleteDraft("ORDER-B");
        JsonArray Evidence((Guid Document, Guid Revision) first, (Guid Document, Guid Revision) second)
            => new(FinancialEvidencePostingTests.Documents(first)[0]!.DeepClone(), FinancialEvidencePostingTests.Documents(second)[0]!.DeepClone());
        firstDraft["revision"]!["documents"] = Evidence(firstDocument, secondDocument);
        secondDraft["revision"]!["documents"] = Evidence(secondDocument, firstDocument);
        var first = await SupplierBillPostingTests.ReviewedAsync(context, firstDraft);
        var second = await SupplierBillPostingTests.ReviewedAsync(context, secondDraft);
        await using var sibling = await context.Journal.OpenSiblingAsync();
        await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "BEGIN TRAN;");
        Task<JsonObject> competing;
        try
        {
            await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(first));
            competing = context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(second), sibling);
            // WHEN both connections compete with reversed input THEN established source coordination serializes them.
            await WaitForBlockedAsync(context, context.Journal.Connection, sibling);
        }
        finally { await FinancialEvidenceRemovalTests.DocumentSqlAsync(context, "COMMIT;"); }
        await competing.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(4, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Storage.Attachments WHERE Held=1"));
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillPostings"));
    }

    internal static async Task WaitForBlockedAsync(SupplierBillTestContext context, SqlConnection holder, SqlConnection contender)
    {
        await using var observer = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var query = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@waiting AND blocking_session_id=@holder AND wait_type LIKE 'LCK%';", observer);
            query.Parameters.AddWithValue("@waiting", contender.ServerProcessId);
            query.Parameters.AddWithValue("@holder", holder.ServerProcessId);
            if ((int)(await query.ExecuteScalarAsync(timeout.Token))! == 1) return;
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
