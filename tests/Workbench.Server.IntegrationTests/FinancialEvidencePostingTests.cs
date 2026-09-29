// Copyright (c) 2026 The White Stag Collection.
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidencePostingTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostingPinsUploadedEvidenceIncludingZeroValueBills(bool zero)
    {
        // GIVEN an actually uploaded document on a reviewed bill; review alone acquires no hold.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await UploadAsync(context, storage);
        var digest = await context.ScalarAsync<string>($"SELECT Sha256 FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document.Document}'");
        await context.AdminAsync("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.policies.retentionYears',7)");
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = Documents(document);
        if (zero)
        {
            draft["revision"]!["total"] = "0";
            foreach (var component in draft["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        Assert.False(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        // WHEN the reviewed source posts through its real restricted boundary.
        var request = Guid.NewGuid(); var command = context.PostCommand(reviewed);
        var result = await context.ExecuteAsync("PostSupplierBill", request, command);
        // THEN the compatibility hold protects uploaded evidence even without a monetary journal.
        Assert.True(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(document.Document, await context.ScalarAsync<Guid>("SELECT DocumentId FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(document.Revision, await context.ScalarAsync<Guid>("SELECT RevisionId FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(digest, await context.ScalarAsync<string>("SELECT Sha256 FROM Accounting.FinancialEvidenceLinks"));
        var original = await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH");
        var sourceHashes = await FinancialHashesAsync(context);
        // AND policy and live label edits cannot rewrite the posted link or extend it on replay.
        await ChangeRetentionAsync(context, 1);
        await context.AdminAsync("UPDATE Purchasing.PurchaseOrderDocuments SET Label='Renamed live document'; UPDATE Purchasing.Suppliers SET Name='Renamed supplier'");
        Assert.Equal(result.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, command)).ToJsonString());
        Assert.Equal(original, await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH"));
        Assert.Equal(sourceHashes, await FinancialHashesAsync(context));
        Assert.Equal(7, await context.ScalarAsync<int>("SELECT RetentionYears FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal("Invoice", await context.ScalarAsync<string>("SELECT Label FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(zero ? 0 : 1, result["journalIds"]!.AsArray().Count);
    }

    internal static Task<string> FinancialHashesAsync(SupplierBillTestContext context) => context.ScalarAsync<string>("""
        SELECT (SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH) sources,
          (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH) journals,
          (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH) lines,
          (SELECT * FROM Accounting.PostingReceipts ORDER BY RequestId FOR JSON PATH) receipts,
          (SELECT * FROM Purchasing.RecognitionSideEvents ORDER BY Id FOR JSON PATH) recognition,
          (SELECT * FROM Purchasing.SupplierPayments ORDER BY Id FOR JSON PATH) payments,
          (SELECT * FROM Purchasing.SupplierBillPostings ORDER BY BillId FOR JSON PATH) bills,
          (SELECT * FROM Purchasing.SupplierFinancialReceipts ORDER BY RequestId FOR JSON PATH) supplierReceipts
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
        """);

    internal static async Task ChangeRetentionAsync(SupplierBillTestContext context, int years)
    {
        var payload = JsonNode.Parse(await context.ScalarAsync<string>("SELECT Payload FROM Accounting.Configurations"))!;
        payload["policies"]!["retentionYears"] = years;
        var version = await context.ScalarAsync<Guid>("SELECT Version FROM Accounting.Configurations");
        await context.Journal.SaveAsync(Guid.NewGuid(), "Configure", payload.ToJsonString(), expectedVersion: version);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("overflow")]
    public async Task FailedPostingCannotLeaveEvidenceOrFinancialWrites(string failure)
    {
        // GIVEN uploaded evidence and a reviewed bill whose final transaction cannot commit.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await UploadAsync(context, storage);
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = Documents(document);
        if (failure == "overflow")
        {
            draft["revision"]!["postingDate"] = "9999-01-01";
            await context.AdminAsync("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.policies.retentionYears',7)");
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        if (failure == "receipt") await context.AdminAsync("CREATE TRIGGER Purchasing.RejectEvidenceReceipt ON Purchasing.SupplierBillReceipts AFTER INSERT AS THROW 51999,'Receipt failure',1;");
        var before = await FinancialHashesAsync(context);
        // WHEN posting fails at retention arithmetic or the final receipt THEN the transaction retains no partial evidence or money.
        Assert.Equal(failure == "receipt" ? 51999 : 51000,
            (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed)))).Number);
        Assert.Equal(before, await FinancialHashesAsync(context));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets"));
        Assert.False(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Fact]
    public async Task PaymentPinsUploadedEvidenceAndUnsetPolicyStaysIndefinite()
    {
        // GIVEN a real uploaded receipt and unresolved retention duration.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await UploadAsync(context.Bills, storage);
        var payment = await context.CommandAsync();
        payment["evidence"] = new JsonObject { ["documents"] = Documents(document), ["missingEvidenceReason"] = null };
        var request = Guid.NewGuid();
        // WHEN it posts and the live funding account and retention setting later change.
        var posted = await context.RecordAsync(payment, request);
        var before = await FinancialHashesAsync(context.Bills);
        var links = await context.Bills.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH");
        await ChangeRetentionAsync(context.Bills, 1);
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET Name='Current renamed account'");
        // THEN replay preserves the indefinite link and every original financial snapshot.
        Assert.Equal(posted.ToJsonString(), (await context.RecordAsync(payment, request)).ToJsonString());
        Assert.Equal(before, await FinancialHashesAsync(context.Bills));
        Assert.Equal(links, await context.Bills.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks WHERE RetentionYears IS NULL AND MinimumRetentionDeadlineUtc IS NULL"));
        Assert.True(await context.Bills.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("legacy")]
    [InlineData("unknown")]
    [InlineData("digest")]
    [InlineData("recovery")]
    [InlineData("pending")]
    [InlineData("reason")]
    public async Task RecognitionRequiresTypedOwnedEvidenceAndNeverTrustsLegacyDocumentStrings(string kind)
    {
        // GIVEN a trusted upstream recognition source and an actually uploaded document.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await UploadAsync(context, storage);
        var command = await context.Recognition.CommandAsync();
        var side = command["units"]![0]!["sides"]![0]!;
        var evidence = side["evidence"]!;
        if (kind == "legacy")
        {
            evidence["documentRevision"] = document.Revision.ToString();
            evidence["documentDigest"] = new string('A', 64);
        }
        else evidence["documents"] = Documents(kind == "unknown" ? (Guid.NewGuid(), document.Revision) : document);
        if (kind == "reason") evidence["documents"] = new JsonArray();
        if (kind == "digest") await context.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET Sha256=REPLICATE('B',64) WHERE Id='{document.Document}'");
        if (kind == "recovery") await context.AdminAsync($"INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc) VALUES('{JournalTestContext.TenantId}','{document.Revision}',NEWID(),1,'Missing',SYSUTCDATETIME())");
        if (kind == "pending") await context.AdminAsync("UPDATE Security.BlobRecoveryState SET IsPending=1");
        await StoreRecognitionEvidenceAsync(context, side);
        var before = await FinancialHashesAsync(context);
        // WHEN the authentic source is posted THEN typed ownership and digest are enforced; strings remain only descriptive.
        if (kind is "unknown" or "digest" or "recovery" or "pending" or "reason")
        {
            Assert.Equal(kind == "reason" ? 51000 : 51004, (await Assert.ThrowsAsync<SqlException>(() => context.Recognition.PostAsync(command.ToJsonString()))).Number);
            Assert.Equal(before, await FinancialHashesAsync(context));
            Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets"));
        }
        else
        {
            await context.Recognition.PostAsync(command.ToJsonString());
            Assert.Equal(kind == "typed" ? 1 : 0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
            Assert.Equal(kind == "legacy", await context.ScalarAsync<bool>("SELECT LegacyEvidence FROM Accounting.FinancialEvidenceSets"));
            if (kind == "legacy") Assert.Equal("Reviewed source evidence", await context.ScalarAsync<string>("SELECT MissingEvidenceReason FROM Accounting.FinancialEvidenceSets"));
        }
    }

    internal static async Task StoreRecognitionEvidenceAsync(SupplierBillTestContext context, JsonNode side)
    {
        await using var connection = new SqlConnection(context.Journal.Application.AdminConnectionString); await connection.OpenAsync();
        await using var update = new SqlCommand("UPDATE Purchasing.FixtureRecognitionSources SET EvidenceJson=@evidence WHERE Id=@id", connection);
        update.Parameters.AddWithValue("@evidence", side["evidence"]!.ToJsonString());
        update.Parameters.AddWithValue("@id", side["sourceId"]!.GetValue<string>());
        await update.ExecuteNonQueryAsync();
    }

    internal static JsonArray Documents((Guid Document, Guid Revision) document) =>
        new(new JsonObject { ["documentId"] = document.Document.ToString(), ["revisionId"] = document.Revision.ToString() });

    internal static async Task<(Guid Document, Guid Revision)> UploadAsync(SupplierBillTestContext context, PurchaseOrderDocumentEndpointTests.TestStorage storage)
    {
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var path = $"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents";
        var version = Convert.ToBase64String(Convert.FromHexString(context.Recognition.PurchaseOrderVersion[2..]));
        using var uploaded = await PurchaseOrderDocumentEndpointTests.UploadAsync(client, path, version, Guid.NewGuid(), DocumentValidatorTests.Pdf());
        uploaded.EnsureSuccessStatusCode();
        var list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        var saved = (await uploaded.Content.ReadFromJsonAsync<PurchaseOrderDocumentOperationResponse>())!;
        var document = Assert.Single(list.Documents, value => value.Id == saved.DocumentId);
        context.Recognition.PurchaseOrderVersion = "0x" + Convert.ToHexString(Convert.FromBase64String(list.OrderVersion));
        var revision = await context.ScalarAsync<Guid>($"SELECT RevisionId FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document.Id}'");
        return (document.Id, revision);
    }
}
