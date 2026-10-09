// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

// Only prerequisite main-schema setup is shared. Each case runs the real BK-07
// migration against its own restored database, with new credentials and sessions.
public class FinancialEvidenceLegacyFixture(SqlServerFixture server) : IAsyncLifetime
{
    private SqlServerFixture.SqlDatabaseTemplate _database = null!;
    private SupplierContextState _state = null!;

    public async Task InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCorrectionFixture.OpenAsync(server, FinancialEvidenceMigrationTests.PriorMigration);
        await PrepareAsync(context);
        var recognition = context.Bills.Recognition;
        _state = new(context.Allocation.Journal.ConfigurationVersion, recognition.PurchaseOrderId,
            recognition.SupplierId, recognition.PurchaseOrderVersion, JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Advance);
        var principal = new SqlConnectionStringBuilder(context.Allocation.Journal.Application.WebConnectionString).UserID;
        await context.Allocation.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]", StringComparison.Ordinal)}];");
        _database = await server.CaptureTemplateAsync(context.Allocation.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared genuine current-main financial baseline in {clock.Elapsed.TotalSeconds:F3}s; included in process wall time.");
    }

    internal async Task<SupplierPaymentTestContext> OpenAsync()
        => await SupplierPaymentTestContext.RestoreAsync(await server.RestoreTemplateAsync(_database), _state);

    internal virtual Task PrepareAsync(SupplierPaymentTestContext context) => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;
}

public sealed class FinancialEvidenceRecoveryFixture(SqlServerFixture server) : FinancialEvidenceLegacyFixture(server)
{
    internal PurchaseOrderDocumentEndpointTests.TestStorage Source { get; } = new();
    internal (Guid Document, Guid Revision) Document { get; private set; }

    internal override async Task PrepareAsync(SupplierPaymentTestContext payment)
    {
        // Recovery owns post-upgrade byte handling; the migration suite independently owns upgrade correctness.
        var context = payment.Bills;
        Document = await FinancialEvidencePostingTests.UploadAsync(context, Source);
        await FinancialEvidenceRemovalTests.PostAsync(context, Document);
        await context.AdminAsync("""
            UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME();
            UPDATE Storage.Attachments SET CurrentRevisionId=NULL,DeletedAtUtc=SYSUTCDATETIME(),DeleteAfterUtc=DATEADD(day,-1,SYSUTCDATETIME());
            UPDATE Storage.Revisions SET State=3;
            """);
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
    }

    public override Task DisposeAsync() { Source.Dispose(); return Task.CompletedTask; }
}

public sealed class FinancialEvidencePaymentLegacyFixture(SqlServerFixture server) : FinancialEvidenceLegacyFixture(server)
{
    internal override async Task PrepareAsync(SupplierPaymentTestContext payment)
    {
        // The upgrade tests own derivation, not the already-covered cost of recording and correcting payments.
        var context = payment.Bills;
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var original = await payment.CommandAsync();
        original["evidence"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        await payment.RecordAsync(original);
        var replacement = await payment.CommandAsync("80", "2026-09-20");
        replacement["evidence"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        await context.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(payment,
            Guid.Parse(original["paymentId"]!.ToString()), replacement));
    }
}

public sealed class FinancialEvidenceRecognitionLegacyFixture(SqlServerFixture server) : FinancialEvidenceLegacyFixture(server)
{
    internal override async Task PrepareAsync(SupplierPaymentTestContext payment)
    {
        var context = payment.Bills.Recognition;
        var original = await PurchaseRecognitionCorrectionTests.MatchedAsync(context);
        await context.CorrectAsync((await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, original, "98")).ToJsonString());
    }
}
