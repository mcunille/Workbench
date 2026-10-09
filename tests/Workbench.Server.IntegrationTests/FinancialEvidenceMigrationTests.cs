// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceMigrationTests(FinancialEvidenceLegacyFixture legacy, FinancialEvidencePaymentLegacyFixture payments)
    : IClassFixture<FinancialEvidenceLegacyFixture>, IClassFixture<FinancialEvidencePaymentLegacyFixture>
{
    internal const string PriorMigration = "20261003214043_AddTenantGemReference";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UpgradePreservesHistoryAndPinsOnlyPostedLegacyEvidence(bool zero, bool reviewOnly)
    {
        // GIVEN an actual merged GEM-06 database and uploaded historical evidence, including a purged SQL lifecycle.
        await using var payment = await legacy.OpenAsync();
        var context = payment.Bills;
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
        Assert.Equal(!reviewOnly, await context.ScalarAsync<bool>("SELECT IsPending FROM Security.BlobRecoveryState"));
        Assert.Equal(!reviewOnly, await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        Assert.Equal(3, await context.ScalarAsync<int>("SELECT CONVERT(int,State) FROM Storage.Revisions"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
        await MigrationHistoryAssertions.AssertCurrentAsync(context.Journal.Application.AdminConnectionString);
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(context.Journal.Application.AdminConnectionString, PriorMigration, default))).Number);
    }

    [Fact]
    public async Task UpgradeKeepsReversedPaymentEvidenceAndReplacementLineage()
    {
        // GIVEN a real legacy correction reversing its original payment and sharing one authenticated receipt with the replacement.
        await using var payment = await payments.OpenAsync();
        var context = payment.Bills;
        var before = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        // WHEN the release derives protection from the immutable original and correction identities.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        // THEN money and hashes are untouched; reversal keeps the original link and successor inheritance.
        Assert.Equal(before, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets WHERE OwnerKind='SupplierPayment'"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets WHERE InheritedEvidenceSetId IS NOT NULL"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks WHERE MinimumRetentionDeadlineUtc IS NULL"));
        Assert.True(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Fact]
    public async Task UpgradeKeepsLegacyRecognitionReferencesUnresolved()
    {
        // GIVEN the predecessor accepts source evidence without BK-07's document authentication.
        await using var payment = await legacy.OpenAsync();
        var context = payment.Bills;
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var command = await context.Recognition.CommandAsync();
        var side = command["units"]![0]!["sides"]![0]!;
        var evidence = side["evidence"]!;
        evidence["documentRevision"] = document.Revision.ToString();
        evidence["documentDigest"] = new string('A', 64);
        await FinancialEvidencePostingTests.StoreRecognitionEvidenceAsync(context, side);
        await context.Recognition.PostAsync(command.ToJsonString());
        var before = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        // WHEN backfill resolves the immutable source rather than treating arbitrary strings as ownership.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        // THEN descriptive revision/digest strings acquire neither a link nor invented mutation authority.
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets WHERE LegacyEvidence=1 AND MutationPermission IS NULL"));
        Assert.False(string.IsNullOrWhiteSpace(await context.ScalarAsync<string>("SELECT MissingEvidenceReason FROM Accounting.FinancialEvidenceSets")));
        Assert.Equal(before, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
    }

    [Theory]
    [InlineData("revisionId")]
    [InlineData("documents")]
    [InlineData("element")]
    public async Task ContradictoryTypedPaymentEvidenceAbortsTheWholeUpgrade(string property)
    {
        // GIVEN genuine posted legacy payment history, with privileged corruption in the isolated source snapshot.
        await using var payment = await payments.OpenAsync();
        var context = payment.Bills;
        var path = property == "documents" ? "$.evidence.documents" : "$.evidence.documents[0].revisionId";
        await context.AdminAsync(property == "element"
            ? "UPDATE Purchasing.SupplierPayments SET EvidenceJson=JSON_MODIFY(EvidenceJson,'$.evidence.documents',JSON_QUERY('[\"invalid\"]'))"
            : $"UPDATE Purchasing.SupplierPayments SET EvidenceJson=JSON_MODIFY(EvidenceJson,'{path}','{Guid.NewGuid()}')");
        var before = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        // WHEN backfill encounters an inconsistent typed reference, it must not downgrade it to missing evidence.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default));
        // THEN the entire release transaction rolls back and leaves the source bytes for operator diagnosis.
        Assert.Equal(51012, error.Number);
        Assert.Contains("SupplierPayment", error.Message, StringComparison.Ordinal);
        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await context.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId='{CurrentSchema.MigrationId}'"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name='FinancialEvidenceSets'"));
        Assert.Equal(before, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
    }
}
