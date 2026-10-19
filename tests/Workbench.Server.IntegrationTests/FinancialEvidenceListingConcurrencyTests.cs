// Copyright (c) 2026 The White Stag Collection.
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Workbench.Server.Authorization;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceListingConcurrencyTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SupplierBillTestContext _context = null!;

    // Each test instance owns a fresh database, credentials and session. Count this cold
    // initialization in process timing while keeping it outside the race's case deadline.
    public async Task InitializeAsync() => _context = await SupplierBillPostingTests.OpenAsync(sqlServer);
    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public async Task DocumentListingAndEvidenceAppendCompleteWithoutAttachmentStateLockInversion()
    {
        // GIVEN linked evidence and another authentic source ready to acquire that same document.
        var context = _context;
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        var second = context.CompleteDraft("INV-LIST-LOCK-ORDER"); second["revision"]!["total"] = "0";
        foreach (var component in second["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, second);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        await FinancialEvidenceSecurityTests.InstallAdapterAsync(context);
        var append = await FinancialEvidenceSecurityTests.CommandAsync(context, Guid.Parse(posted["billId"]!.GetValue<string>()), document);
        var versionBefore = await context.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Storage.FinancialEvidenceAttachmentStates");
        var pause = new BeforeIndependentHoldRead();
        var tenant = new TenantContext(JournalTestContext.TenantId);
        await using var database = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(context.Journal.Application.WebConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor(tenant, new TenantContextProof(context.Journal.ProofKey)), pause)
            .Options, tenant);
        await database.Database.OpenConnectionAsync();
        var readerConnection = (SqlConnection)database.Database.GetDbConnection();
        var actor = new RequestActor(JournalTestContext.ActorId, JournalTestContext.TenantId,
            context.Journal.SessionId, new HashSet<string>());
        var service = new PurchaseOrderDocumentService(database, storage.Store, actor);

        // WHEN listing has read membership and its token, pause before its real attachment read.
        var listing = service.ListAsync(context.Recognition.PurchaseOrderId, default);
        Task<string>? writing = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            writing = FinancialEvidenceSecurityTests.AppendAsync(context, append, Guid.NewGuid());
            await FinancialEvidenceConcurrencyTests.WaitForBlockedAsync(context, readerConnection, context.Journal.Connection);
        }
        finally { pause.Resume.TrySetResult(); }

        // THEN both actual operations complete, and listing keeps the coherent earlier evidence token.
        // Without read coordination, append owns attachment X and waits for listing's state S;
        // resuming listing requests attachment S and produces a real SQL deadlock.
        await Task.WhenAll(listing, writing!).WaitAsync(TimeSpan.FromSeconds(15));
        var retained = Assert.Single((await listing).Documents).Retention!;
        Assert.True(retained.Retained);
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(versionBefore[2..])), retained.EvidenceVersion);
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        var versionAfter = await context.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Storage.FinancialEvidenceAttachmentStates");
        Assert.NotEqual(versionBefore, versionAfter);
    }

    private sealed class BeforeIndependentHoldRead : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Storage].[Attachments]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[IndependentHeld]", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
