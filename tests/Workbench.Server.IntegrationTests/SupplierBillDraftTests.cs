// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillDraftTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualInvoiceRequiresNewBillAndDuplicateResolution(bool reviewed)
    {
        // GIVEN a draft or reviewed pro forma retained as its own non-posting source.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft(kind: "ProForma");
        var original = await context.SaveAsync(Guid.NewGuid(), draft);
        if (reviewed) original = await context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(original));
        var id = Guid.Parse(original["billId"]!.ToString());
        var before = await context.ReadAsync("ReadSupplierBill", ("@BillId", id));
        var invoice = context.CompleteDraft();
        // WHEN an actual invoice is submitted as a revision of that pro forma.
        var conversion = context.Change(original, "Revise", invoice["revision"]!.AsObject());
        // THEN conversion rejects without changing the source, review pointer or command history.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), conversion))).Number);
        Assert.Equal(before.ToJsonString(), (await context.ReadAsync("ReadSupplierBill", ("@BillId", id))).ToJsonString());
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillRevisions"));
        Assert.Equal(reviewed ? 2 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillReceipts"));
        // AND ordinary pro forma editing still works, while a new invoice explicitly resolves its predecessor's duplicate.
        var edited = await context.SaveAsync(Guid.NewGuid(), context.Change(original, "Revise", draft["revision"]!.AsObject()));
        invoice["revision"]!["predecessorBillId"] = id.ToString();
        var actual = await context.SaveAsync(Guid.NewGuid(), invoice);
        Assert.NotEqual(id.ToString(), actual["billId"]!.ToString());
        var review = context.ReviewCommand(actual);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ReviewAsync(Guid.NewGuid(), review))).Number);
        review["resolutions"] = new JsonArray(new JsonObject
        {
            ["conflictingBillId"] = edited["billId"]!.DeepClone(),
            ["conflictingRevisionId"] = edited["revisionId"]!.DeepClone(),
            ["reason"] = "Actual invoice follows the separately retained pro forma"
        });
        actual = await context.ReviewAsync(Guid.NewGuid(), review);
        Assert.Equal("Posted", (await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(actual)))["state"]!.ToString());
        var retained = await context.ReadAsync("ReadSupplierBill", ("@BillId", id));
        Assert.Equal("ProForma", retained["revision"]!["kind"]!.ToString());
        Assert.Null(retained["posting"]);
    }

    [Fact]
    public async Task DraftRevisionPreservesPriorBytesAndExactRetry()
    {
        // GIVEN an incomplete bill draft on an ordered purchase.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var input = context.DraftCommand(); var request = Guid.NewGuid();
        var created = await context.SaveAsync(request, input);
        Assert.Equal("Draft", created["state"]?.GetValue<string>());
        Assert.Matches("^0x[0-9A-F]{16}$", created["version"]!.GetValue<string>());
        var original = await context.ScalarAsync<string>("SELECT Payload FROM Purchasing.SupplierBillRevisions");
        // WHEN a later edit changes its reference and the original request is retried.
        input["revision"]!["reference"] = "INV-2";
        var edited = await context.SaveAsync(Guid.NewGuid(), context.Change(created, "Revise", input["revision"]!.AsObject()));
        input["revision"]!["reference"] = "INV-1";
        var replay = await context.SaveAsync(request, input);
        // THEN immutable prior source and receipt survive without rewinding the head.
        Assert.Equal(created.ToJsonString(), replay.ToJsonString());
        Assert.NotEqual(created["revisionId"]!.ToString(), edited["revisionId"]!.ToString());
        Assert.Equal(original, await context.ScalarAsync<string>("SELECT Payload FROM Purchasing.SupplierBillRevisions WHERE Sequence=1"));
        Assert.Equal(2L, await context.ScalarAsync<long>("SELECT MAX(Sequence) FROM Purchasing.SupplierBillRevisions"));
        input["revision"]!["reference"] = "changed retry";
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(request, input))).Number);
    }

    [Fact]
    public async Task AbandonedBillRejectsFurtherEdits()
    {
        // GIVEN a draft that was entered in error.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var input = context.DraftCommand(); var created = await context.SaveAsync(Guid.NewGuid(), input);
        Assert.Equal("Draft", created["state"]?.GetValue<string>());
        // WHEN it is abandoned, then someone attempts to revise it.
        var abandoned = await context.SaveAsync(Guid.NewGuid(), context.Change(created, "Abandon"));
        var error = await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), context.Change(abandoned, "Revise", input["revision"]!.AsObject())));
        // THEN abandonment and the single source revision remain durable.
        Assert.Equal(51009, error.Number);
        Assert.Equal("Abandoned", abandoned["state"]!.GetValue<string>());
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillRevisions"));
    }

    [Theory]
    [InlineData(" \tab-001\r\n X ", "AB-001 X")]
    [InlineData("AB001", "AB001")]
    [InlineData("AB-01", "AB-01")]
    [InlineData("ä", "ä")]
    [InlineData("Ä", "Ä")]
    public async Task ReferenceNormalizationIsConservative(string reference, string expected)
    {
        // GIVEN a supplier reference whose original bytes must survive normalization.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        // WHEN the source draft is saved.
        var result = await context.SaveAsync(Guid.NewGuid(), context.DraftCommand(reference));
        // THEN the durable key has only the documented ASCII transformations.
        Assert.Equal("Draft", result["state"]?.GetValue<string>());
        Assert.Equal(expected, await context.ScalarAsync<string>("SELECT NormalizedReference FROM Purchasing.SupplierBillRevisions"));
    }
}
