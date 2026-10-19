// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using Workbench.Server.Identity;
using Workbench.Server.Operations;
using Workbench.Server.Purchasing;
using Workbench.Server.Persistence;
using Workbench.Server.Authorization;
using Workbench.Server.Tenancy;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceRemovalTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("prepare")]
    [InlineData("attachment")]
    [InlineData("revision")]
    [InlineData("document")]
    [InlineData("hold")]
    [InlineData("pointer")]
    [InlineData("complete")]
    [InlineData("lock")]
    [InlineData("replay")]
    public async Task PostedEvidenceRejectsEveryDeletionAuthority(string path)
    {
        // GIVEN authentic posted evidence with actual immutable provider bytes.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await PostAsync(context, document);
        var attachment = await context.ScalarAsync<Guid>("SELECT AttachmentId FROM Accounting.FinancialEvidenceLinks");
        var work = Guid.NewGuid(); var owner = Guid.NewGuid();
        await context.AdminAsync($"""
            INSERT Operations.WorkItems(Id,TenantId,Kind,AttachmentId,State,Attempts,Generation,CreatedAtUtc,AvailableAtUtc,LeaseOwner,LeaseExpiresAtUtc)
            VALUES('{work}','{JournalTestContext.TenantId}',1,'{attachment}',{(path == "replay" ? 3 : 1)},1,1,SYSUTCDATETIME(),SYSUTCDATETIME(),'{owner}',DATEADD(hour,1,SYSUTCDATETIME()));
            """);
        var command = path switch
        {
            "prepare" => PrepareSql(context, document.Document, Guid.NewGuid()),
            "attachment" => "UPDATE Storage.Attachments SET DeletedAtUtc=SYSUTCDATETIME(),DeleteAfterUtc=SYSUTCDATETIME()",
            "revision" => "UPDATE Storage.Revisions SET State=3",
            "document" => "UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME()",
            "hold" => "UPDATE Storage.Attachments SET Held=0",
            "pointer" => "UPDATE Storage.Attachments SET CurrentRevisionId=NULL",
            "complete" => $"EXEC Operations.CompleteWork '{work}','{owner}',1",
            "lock" => $"BEGIN TRAN; EXEC Operations.LockWork '{work}','{owner}',1; COMMIT;",
            _ => $"EXEC Storage.ReplayDeletion '{work}'"
        };
        // WHEN an existing SQL lifecycle entry point attempts deletion without disposal authority.
        var error = await Assert.ThrowsAsync<SqlException>(() => path == "prepare" ? DocumentSqlAsync(context, command) : context.AdminAsync(command));
        // THEN neither live visibility, immutable bytes nor effective hold can be lost.
        Assert.Equal(51011, error.Number);
        await AssertPreservedAsync(context, storage, document.Revision);
        Assert.NotEqual("Completed", await context.ScalarAsync<string>("SELECT COALESCE(Outcome,'') FROM Operations.WorkItems WHERE Kind=1"));
    }

    [Fact]
    public async Task GenericAttachmentDeletionCannotBypassPostedRetention()
    {
        // GIVEN a financially linked attachment and an actor allowed to manage generic attachments.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await PostAsync(context, document);
        var attachment = await context.ScalarAsync<Guid>("SELECT AttachmentId FROM Accounting.FinancialEvidenceLinks");
        await using var database = BlobPersistenceTests.CreateContext(context.Journal.Application.WebConnectionString,
            new TenantContextProof(context.Journal.ProofKey), JournalTestContext.TenantId);
        var actor = new RequestActor(JournalTestContext.ActorId, JournalTestContext.TenantId, context.Journal.SessionId,
            new HashSet<string> { AttachmentService.ManagePermission });
        // WHEN the generic lifecycle service attempts logical deletion THEN SQL preserves the posted evidence.
        var error = await Record.ExceptionAsync(() => new AttachmentService(database, storage.Store, actor).DeleteAsync(attachment, document.Revision, default));
        Assert.NotNull(error);
        Assert.Equal(51011, Assert.IsType<SqlException>(error is DbUpdateException update ? update.InnerException : error).Number);
        await AssertPreservedAsync(context, storage, document.Revision);
    }

    [Fact]
    public async Task OrdinaryRemoveReturnsRecoverableRetentionConflict()
    {
        // GIVEN an authenticated PO document workflow whose invoice has posted.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await PostAsync(context, document);
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient(); await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var path = $"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents";
        var list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        // WHEN ordinary Remove reaches the HTTP contract THEN the client receives the stable retention code.
        var response = await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Delete, $"{path}/{document.Document}",
            new ChangePurchaseOrderDocumentRequest(Guid.NewGuid(), list.OrderVersion, Assert.Single(list.Documents).Version, null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("financial_evidence_retained", await response.Content.ReadAsStringAsync());
        await AssertPreservedAsync(context, storage, document.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCleanupPreservesIndependentHoldAndPostedBytes(bool upgrade)
    {
        // GIVEN an independently held upload and deletion work queued before financial capture.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, upgrade ? "20260928071548_AddSupplierOpenItems" : null);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await context.AdminAsync($"""
            UPDATE Storage.Attachments SET Held=1{(upgrade ? "" : ",IndependentHeld=1")};
            INSERT Operations.WorkItems(Id,TenantId,Kind,AttachmentId,State,Attempts,Generation,CreatedAtUtc,AvailableAtUtc,LeaseOwner,LeaseExpiresAtUtc)
              SELECT NEWID(),TenantId,1,Id,1,1,1,SYSUTCDATETIME(),SYSUTCDATETIME(),NEWID(),DATEADD(day,-1,SYSUTCDATETIME()) FROM Storage.Attachments;
            """);
        if (upgrade) await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        await PostAsync(context, document);
        // WHEN a restricted worker claims the old work item THEN neither guard releases the independent hold.
        var worker = new WorkProcessor(await context.Journal.Application.CreateWorkerConnectionAsync(), new TenantContextProof(context.Journal.ProofKey),
            new EphemeralDataProtectionProvider(), new DisabledIdentityMessageDelivery(), new Dictionary<string, IBlobStore> { [storage.Store.Alias] = storage.Store });
        Assert.True(await worker.RunOnceAsync(default));
        Assert.True(await context.ScalarAsync<bool>("SELECT IndependentHeld FROM Storage.Attachments"));
        Assert.Equal("Rejected", await context.ScalarAsync<string>("SELECT Outcome FROM Operations.WorkItems WHERE Kind=1"));
        await AssertPreservedAsync(context, storage, document.Revision);
    }

    [Fact]
    public async Task PreparedRemovalCannotHideEvidencePostedBeforeCompletion()
    {
        // GIVEN a removal prepared while the uploaded document is still unposted.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var request = Guid.NewGuid();
        await DocumentSqlAsync(context, PrepareSql(context, document.Document, request));
        await using var completing = await context.Journal.OpenSiblingAsync();
        await using var finish = new SqlCommand($"BEGIN TRAN; EXEC Purchasing.FinishPurchaseOrderDocument '{request}',0; COMMIT;", completing);
        await DocumentSqlAsync(context, "BEGIN TRAN;");
        Task<int> completion;
        try
        {
            await PostAsync(context, document);
            // WHEN independent completion waits on the posting transaction that acquired retention.
            completion = finish.ExecuteNonQueryAsync();
            await FinancialEvidenceConcurrencyTests.WaitForBlockedAsync(context, context.Journal.Connection, completing);
        }
        finally { await DocumentSqlAsync(context, "COMMIT;"); }
        await completion.WaitAsync(TimeSpan.FromSeconds(15));
        // THEN it records a durable conflict while the evidence remains visible and readable.
        Assert.Equal(2, await context.ScalarAsync<int>($"SELECT State FROM Purchasing.PurchaseOrderDocumentOperations WHERE RequestId='{request}'"));
        await AssertPreservedAsync(context, storage, document.Revision);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Operations.WorkItems WHERE Kind=1"));
        // AND retrying that durable removal conflict explains the retention route to the HTTP caller.
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient(); await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var path = $"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents";
        var list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        var response = await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Delete, $"{path}/{document.Document}",
            new ChangePurchaseOrderDocumentRequest(request, list.OrderVersion, Assert.Single(list.Documents).Version, null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("financial_evidence_retained", await response.Content.ReadAsStringAsync());
    }

    internal static async Task PostAsync(SupplierBillTestContext context, (Guid Document, Guid Revision) document)
    {
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
    }

    internal static async Task DocumentSqlAsync(SupplierBillTestContext context, string sql)
    {
        await using var command = new SqlCommand(sql, context.Journal.Connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static string PrepareSql(SupplierBillTestContext context, Guid document, Guid request) => $"""
        BEGIN TRAN;
        DECLARE @po binary(8)=(SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id='{context.Recognition.PurchaseOrderId}'),
          @doc binary(8)=(SELECT RowVersion FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}');
        EXEC Purchasing.PreparePurchaseOrderDocument @OrderId='{context.Recognition.PurchaseOrderId}',@RequestId='{request}',
          @ExpectedOrderVersion=@po,@Kind=2,@DocumentId='{document}',@ExpectedDocumentVersion=@doc,@ActorUserId='{JournalTestContext.ActorId}';
        COMMIT;
        """;

    internal static async Task AssertPreservedAsync(SupplierBillTestContext context, PurchaseOrderDocumentEndpointTests.TestStorage storage, Guid revision)
    {
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Storage.Attachments WHERE DeletedAtUtc IS NOT NULL"));
        Assert.Equal(1, await context.ScalarAsync<int>($"SELECT State FROM Storage.Revisions WHERE Id='{revision}'"));
        Assert.True(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        await using var content = await storage.Store.OpenReadAsync(new BlobObjectId(JournalTestContext.TenantId, revision), default);
        using var bytes = new MemoryStream(); await content.CopyToAsync(bytes);
        Assert.Equal(DocumentValidatorTests.Pdf(), bytes.ToArray());
    }
}
