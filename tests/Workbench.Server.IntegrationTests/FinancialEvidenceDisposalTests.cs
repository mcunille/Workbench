// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceDisposalTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SharedDeadlineBoundaryIncludesExactExpiryAndRejectsPriorTickOrIndefiniteLink()
    {
        // GIVEN authentic evidence with a finite SQL policy deadline.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await ExpireAsync(context);
        // WHEN evaluated at the exact deadline and immediately before it THEN expiry is inclusive, never early.
        Assert.True(await context.ScalarAsync<bool>("SELECT Storage.FinancialEvidenceRetentionElapsed(TenantId,AttachmentId,MinimumRetentionDeadlineUtc) FROM Accounting.FinancialEvidenceLinks"));
        Assert.False(await context.ScalarAsync<bool>("SELECT Storage.FinancialEvidenceRetentionElapsed(TenantId,AttachmentId,DATEADD(nanosecond,-100,MinimumRetentionDeadlineUtc)) FROM Accounting.FinancialEvidenceLinks"));
        await context.AdminAsync("DISABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks; UPDATE Accounting.FinancialEvidenceLinks SET RetentionYears=NULL,MinimumRetentionDeadlineUtc=NULL; ENABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;");
        // AND null policy never expires even at the maximum representable instant.
        Assert.False(await context.ScalarAsync<bool>("SELECT Storage.FinancialEvidenceRetentionElapsed(TenantId,AttachmentId,CONVERT(datetimeoffset,'9999-12-31T23:59:59.9999999Z')) FROM Accounting.FinancialEvidenceLinks"));
    }
    [Fact]
    public async Task CleanupAfterGraceRechecksReceiptMembershipAndNewIndependentHold()
    {
        // GIVEN a legitimately disposed document, with fixture-owned elapsed time in this disposable database.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await ExpireAsync(context);
        await ExecuteAsync(context, await CommandAsync(context, document.Document, Guid.NewGuid()));
        await context.AdminAsync("""
            DISABLE TRIGGER Accounting.PreserveFinancialEvidenceDisposals ON Accounting.FinancialEvidenceDisposals;
            DISABLE TRIGGER Accounting.PreserveFinancialEvidenceReceipts ON Accounting.FinancialEvidenceReceipts;
            DISABLE TRIGGER Storage.BindFinancialDisposal ON Storage.Attachments;
            DISABLE TRIGGER Purchasing.BindFinancialDisposal ON Purchasing.PurchaseOrderDocuments;
            DECLARE @past datetimeoffset=DATEADD(day,-8,SYSUTCDATETIME());
            UPDATE Accounting.FinancialEvidenceDisposals SET RemovedAtUtc=@past,DeleteAfterUtc=DATEADD(day,7,@past);
            UPDATE Accounting.FinancialEvidenceReceipts SET RecordedAtUtc=@past WHERE Operation='Dispose';
            UPDATE Storage.Attachments SET DeletedAtUtc=@past,DeleteAfterUtc=DATEADD(day,7,@past);
            UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=@past;
            UPDATE Operations.WorkItems SET AvailableAtUtc=DATEADD(day,7,@past) WHERE Kind=1;
            ENABLE TRIGGER Accounting.PreserveFinancialEvidenceDisposals ON Accounting.FinancialEvidenceDisposals;
            ENABLE TRIGGER Accounting.PreserveFinancialEvidenceReceipts ON Accounting.FinancialEvidenceReceipts;
            ENABLE TRIGGER Storage.BindFinancialDisposal ON Storage.Attachments;
            ENABLE TRIGGER Purchasing.BindFinancialDisposal ON Purchasing.PurchaseOrderDocuments;
            UPDATE Storage.Attachments SET Held=1,IndependentHeld=1;
            """);
        var worker = new Workbench.Server.Operations.WorkProcessor(await context.Journal.Application.CreateWorkerConnectionAsync(),
            new Workbench.Server.Tenancy.TenantContextProof(context.Journal.ProofKey), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            new Workbench.Server.Identity.DisabledIdentityMessageDelivery(), new Dictionary<string, Workbench.Server.Storage.IBlobStore> { [storage.Store.Alias] = storage.Store });
        // WHEN a new independent hold exists during grace THEN a real worker cannot delete the bytes.
        Assert.True(await worker.RunOnceAsync(default));
        await using (var bytes = await storage.Store.OpenReadAsync(new(JournalTestContext.TenantId, document.Revision), default)) Assert.True(bytes.CanRead);
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT State FROM Storage.Revisions"));
        // AND removing one receipt member fails closed even after grace and hold release.
        await context.AdminAsync("""
            UPDATE Storage.Attachments SET Held=0,IndependentHeld=0;
            DISABLE TRIGGER Accounting.PreserveFinancialEvidenceDisposalLinks ON Accounting.FinancialEvidenceDisposalLinks;
            DELETE Accounting.FinancialEvidenceDisposalLinks;
            ENABLE TRIGGER Accounting.PreserveFinancialEvidenceDisposalLinks ON Accounting.FinancialEvidenceDisposalLinks;
            """);
        Assert.False(await context.ScalarAsync<bool>("SELECT Storage.FinancialEvidencePhysicalDeletionAuthorized(TenantId,Id) FROM Storage.Attachments"));
        await context.AdminAsync("INSERT Accounting.FinancialEvidenceDisposalLinks SELECT l.TenantId,d.RequestId,l.Id FROM Accounting.FinancialEvidenceLinks l JOIN Accounting.FinancialEvidenceDisposals d ON d.TenantId=l.TenantId AND d.AttachmentId=l.AttachmentId; DECLARE @work uniqueidentifier=(SELECT Id FROM Operations.WorkItems WHERE Kind=1); EXEC Storage.ReplayDeletion @work;");
        // WHEN the complete receipt and elapsed grace are restored THEN physical cleanup can finish without losing metadata.
        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal(3, await context.ScalarAsync<int>("SELECT State FROM Storage.Revisions"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.Store.OpenReadAsync(new(JournalTestContext.TenantId, document.Revision), default));
    }
    [Theory]
    [InlineData("future", 51011)]
    [InlineData("indefinite", 51011)]
    [InlineData("hold", 51011)]
    [InlineData("stale-order", 51009)]
    [InlineData("stale-document", 51009)]
    [InlineData("stale-evidence", 51009)]
    [InlineData("blank", 51000)]
    [InlineData("long", 51000)]
    [InlineData("permission", 50903)]
    public async Task DisposalRejectsRetentionAuthorityAndInputConflicts(string condition, int errorNumber)
    {
        // GIVEN a real retained document and a specific independent disposal blocker.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        if (condition != "indefinite") await ExpireAsync(context);
        var reason = condition == "blank" ? "\u2007\u2028\u3000" : condition == "long" ? new string('x', 2001) : "Retention period completed";
        var command = await CommandAsync(context, document.Document, Guid.NewGuid(), reason);
        if (condition == "future") await context.AdminAsync("DISABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks; UPDATE Accounting.FinancialEvidenceLinks SET MinimumRetentionDeadlineUtc=DATEADD(year,1,SYSUTCDATETIME()); ENABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;");
        if (condition == "hold") await context.AdminAsync("UPDATE Storage.Attachments SET IndependentHeld=1");
        if (condition == "stale-order") await context.AdminAsync("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=SYSUTCDATETIME()");
        if (condition == "stale-document") await context.AdminAsync("UPDATE Purchasing.PurchaseOrderDocuments SET Label='New label'");
        if (condition == "stale-evidence") await context.AdminAsync("UPDATE Storage.FinancialEvidenceAttachmentStates SET AttachmentId=AttachmentId");
        if (condition == "permission") await context.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='AccountingConfigurationManage'");
        // WHEN disposal is requested THEN it cannot partially remove visibility or create a receipt.
        Assert.Equal(errorNumber, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(context, command))).Number);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceReceipts WHERE Operation='Dispose'"));
        await FinancialEvidenceRemovalTests.AssertPreservedAsync(context, storage, document.Revision);
    }
    [Fact]
    public async Task ExpiredDisposalPreservesFinancialHistoryAndReplaysOneReceipt()
    {
        // GIVEN authenticated evidence whose complete finite policy has expired.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await ExpireAsync(context);
        var before = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        var request = Guid.NewGuid(); var sql = await CommandAsync(context, document.Document, request);
        // WHEN disposal commits and its exact request is replayed after removal.
        var result = await ExecuteAsync(context, sql);
        Assert.Equal(result, await ExecuteAsync(context, sql));
        // THEN one immutable receipt preserves all identities and schedules seven days of grace.
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceReceipts WHERE Operation='Dispose'"));
        Assert.Equal(await context.ScalarAsync<string>("SELECT Id FROM Accounting.FinancialEvidenceLinks ORDER BY Id FOR JSON PATH"),
            await context.ScalarAsync<string>("SELECT LinkId Id FROM Accounting.FinancialEvidenceDisposalLinks ORDER BY LinkId FOR JSON PATH"));
        Assert.True(await context.ScalarAsync<bool>("SELECT CONVERT(bit,CASE WHEN a.DeleteAfterUtc>=DATEADD(day,7,r.RecordedAtUtc) THEN 1 ELSE 0 END) FROM Storage.Attachments a CROSS JOIN Accounting.FinancialEvidenceReceipts r WHERE r.Operation='Dispose'"));
        Assert.Equal(before, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NULL"));
        // AND the new immutable records remain tenant-scoped and deny runtime DML.
        foreach (var table in new[] { "FinancialEvidenceDisposals", "FinancialEvidenceDisposalLinks" })
        {
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(context, $"DELETE Accounting.{table}"))).Number);
            await using var other = await context.Journal.OpenOtherTenantAsync();
            await using var hidden = new SqlCommand($"SELECT COUNT(*) FROM Accounting.{table}", other);
            Assert.Equal(0, (int)(await hidden.ExecuteScalarAsync())!);
        }
        // AND receipt authority cannot shorten grace, rewrite document identity, or purge early.
        foreach (var mutation in new[] { "UPDATE Storage.Attachments SET DeleteAfterUtc=SYSUTCDATETIME()",
            "UPDATE Storage.Attachments SET DeletedAtUtc=DATEADD(day,-8,SYSUTCDATETIME())", "UPDATE Storage.Attachments SET CurrentRevisionId=NULL",
            "UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=DATEADD(day,-8,SYSUTCDATETIME())", "UPDATE Storage.Revisions SET State=3" })
            Assert.Equal(51011, (await Assert.ThrowsAsync<SqlException>(() => context.AdminAsync(mutation))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(context, sql.Replace("Retention period completed", "Different reason", StringComparison.Ordinal)))).Number);
        // AND current permission is required even after a successful receipt has become durable.
        await context.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='AccountingConfigurationManage'");
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(context, sql))).Number);
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(context,
            $"EXEC Purchasing.ReadRetainedDocumentDisposal '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{context.Recognition.PurchaseOrderId}','{request}'"))).Number);
    }

    internal static Task ExpireAsync(SupplierBillTestContext context) => context.AdminAsync("""
        DISABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;
        UPDATE Accounting.FinancialEvidenceLinks SET RetentionYears=1,AnchorAtUtc='2020-01-01',MinimumRetentionDeadlineUtc='2021-01-01';
        ENABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;
        """);

    internal static async Task<string> CommandAsync(SupplierBillTestContext context, Guid document, Guid request, string reason = "Retention period completed")
    {
        var po = await context.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.DraftOrders WHERE Id='{context.Recognition.PurchaseOrderId}'");
        var doc = await context.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}'");
        var evidence = await context.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Storage.FinancialEvidenceAttachmentStates");
        return $"EXEC Purchasing.DisposeRetainedDocument '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{request}','{context.Recognition.PurchaseOrderId}','{document}',{po},{doc},{evidence},N'{reason.Replace("'", "''", StringComparison.Ordinal)}'";
    }
    internal static async Task<string> ExecuteAsync(SupplierBillTestContext context, string sql, SqlConnection? connection = null)
    {
        await using var command = new SqlCommand(sql, connection ?? context.Journal.Connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
