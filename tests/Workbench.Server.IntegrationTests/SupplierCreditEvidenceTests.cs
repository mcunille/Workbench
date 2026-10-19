// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditEvidenceTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreditCapturesExactPrivateRevisionOrRollsBackUnavailableEvidence(bool removed)
    {
        // GIVEN a recognized bill and one immutable private document revision.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var document = await context.Bills.SeedDocumentAsync();
        var input = await context.CreditCommandAsync(bill, "18");
        input["evidence"] = new JsonObject
        {
            ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.DocumentId.ToString(), ["revisionId"] = document.RevisionId.ToString() }),
            ["missingEvidenceReason"] = null
        };
        var before = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries");
        if (removed) await context.Bills.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME() WHERE Id='{document.DocumentId}'");

        // WHEN posting THEN unavailable evidence rejects without a partial source, journal or hold.
        if (removed)
        {
            Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(input.ToJsonString()))).Number);
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
            Assert.Equal(before, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries"));
            Assert.False(await context.Bills.ScalarAsync<bool>($"SELECT Held FROM Storage.Attachments a JOIN Purchasing.PurchaseOrderDocuments d ON d.AttachmentId=a.Id WHERE d.Id='{document.DocumentId}'"));
            return;
        }
        var result = await context.ExecuteRawAsync(input.ToJsonString());
        var id = Guid.Parse(result["sourceId"]!.GetValue<string>());
        // AND available evidence has one source-authenticated exact revision/digest link and a physical-cleanup hold.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets s JOIN Accounting.FinancialEvidenceLinks l ON l.TenantId=s.TenantId AND l.EvidenceSetId=s.Id WHERE s.OwnerKind='SupplierCredit' AND s.OwnerId='{id}' AND l.DocumentId='{document.DocumentId}' AND l.RevisionId='{document.RevisionId}' AND l.Sha256=REPLICATE('A',64) AND l.Length=4"));
        Assert.True(await context.Bills.ScalarAsync<bool>($"SELECT Held FROM Storage.Attachments a JOIN Purchasing.PurchaseOrderDocuments d ON d.AttachmentId=a.Id WHERE d.Id='{document.DocumentId}'"));
    }
}
