// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceMigrationTests(SqlServerFixture sqlServer)
{
    internal const string PriorMigration = "20261003214043_AddTenantGemReference";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UpgradePreservesHistoryAndPinsOnlyPostedLegacyEvidence(bool zero, bool reviewOnly)
    {
        // GIVEN an actual merged GEM-06 database and uploaded historical evidence, including a purged SQL lifecycle.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, PriorMigration);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        if (zero)
        {
            draft["revision"]!["total"] = "0";
            foreach (var component in draft["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        if (!reviewOnly) await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        await context.AdminAsync("""
            UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME();
            UPDATE Storage.Attachments SET CurrentRevisionId=NULL,DeletedAtUtc=SYSUTCDATETIME(),DeleteAfterUtc=DATEADD(day,-1,SYSUTCDATETIME());
            UPDATE Storage.Revisions SET State=3;
            """);
        var before = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        // WHEN the one forward release migration upgrades the genuine merged baseline.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        // THEN original source and financial bytes survive, and only posted history acquires indefinite protection.
        Assert.Equal(before, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        Assert.Equal(reviewOnly ? 0 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks WHERE MinimumRetentionDeadlineUtc IS NULL AND RetentionYears IS NULL"));
        Assert.Equal(!reviewOnly, await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        Assert.Equal(3, await context.ScalarAsync<int>("SELECT CONVERT(int,State) FROM Storage.Revisions"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
        await MigrationHistoryAssertions.AssertCurrentAsync(context.Journal.Application.AdminConnectionString);
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(context.Journal.Application.AdminConnectionString, PriorMigration, default))).Number);
    }
}
