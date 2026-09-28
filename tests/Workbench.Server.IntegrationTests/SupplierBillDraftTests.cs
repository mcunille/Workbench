// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillDraftTests(SqlServerFixture sqlServer)
{
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
