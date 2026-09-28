// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillReviewTests(SqlServerFixture sqlServer)
{
    private static async Task<SupplierBillTestContext> OpenAsync(SqlServerFixture fixture)
    {
        var context = await SupplierBillTestContext.OpenAsync(fixture);
        await context.AdminAsync("""
            GRANT EXECUTE ON Purchasing.ReviewSupplierBill TO workbench_web;
            """);
        return context;
    }
    [Fact]
    public async Task ReviewAndEditPreserveSourceHistoryWithoutPosting()
    {
        // GIVEN a complete invoice with explicit missing-file rationale.
        await using var context = await OpenAsync(sqlServer);
        var draft = context.CompleteDraft();
        var saved = await context.SaveAsync(Guid.NewGuid(), draft);
        // WHEN it is reviewed and subsequently edited.
        var reviewed = await context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(saved));
        Assert.Equal("Reviewed", reviewed["state"]?.GetValue<string>());
        var edited = await context.SaveAsync(Guid.NewGuid(), context.Change(reviewed, "Revise", draft["revision"]!.AsObject()));
        // THEN the edit requires another review and no financial event has been created.
        Assert.Equal("Draft", edited["state"]!.GetValue<string>());
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillReviews"));
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
    }
    [Theory]
    [InlineData("total", "109.99")]
    [InlineData("documentDate", "2026-02-02")]
    [InlineData("terms", null)]
    [InlineData("missingEvidenceReason", null)]
    public async Task ReviewRequiresCompleteReviewedSource(string field, string? value)
    {
        // GIVEN a well-shaped draft missing one posting prerequisite.
        await using var context = await OpenAsync(sqlServer);
        var draft = context.CompleteDraft();
        if (field == "documentDate") draft["revision"]!["dueDate"] = "2026-02-01";
        if (value is null) draft["revision"]!.AsObject().Remove(field); else draft["revision"]![field] = value;
        var saved = await context.SaveAsync(Guid.NewGuid(), draft);
        // WHEN it is reviewed THEN no review or payable is committed.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(saved)))).Number);
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
    }
    [Fact]
    public async Task DuplicateResolutionTracksExactConflictingRevision()
    {
        // GIVEN two distinct supplier records using the same normalized reference.
        await using var context = await OpenAsync(sqlServer);
        var first = await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft("inv-1"));
        var second = await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft(" INV-1 ", "ProForma"));
        var review = context.ReviewCommand(first);
        // WHEN review omits its conflicting source THEN it fails until that exact revision is resolved.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ReviewAsync(Guid.NewGuid(), review))).Number);
        review["resolutions"] = new JsonArray(new JsonObject
        {
            ["conflictingBillId"] = second["billId"]!.DeepClone(),
            ["conflictingRevisionId"] = second["revisionId"]!.DeepClone(),
            ["reason"] = "The other document is the pro forma preceding this actual invoice"
        });
        Assert.Equal("Reviewed", (await context.ReviewAsync(Guid.NewGuid(), review))["state"]!.GetValue<string>());
    }
    [Fact]
    public async Task ProFormaReviewCreatesNoJournal()
    {
        // GIVEN a reviewed non-posting supplier request.
        await using var context = await OpenAsync(sqlServer);
        var saved = await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft(kind: "ProForma"));
        // WHEN review completes THEN it records evidence without a payable.
        Assert.Equal("Reviewed", (await context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(saved)))["state"]?.GetValue<string>());
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
    }
}
