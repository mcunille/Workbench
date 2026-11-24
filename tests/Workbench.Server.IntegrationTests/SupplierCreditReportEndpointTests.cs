// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditReportEndpointTests(SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Fact]
    public async Task AuthorizedDetailsShowTypedCreditAndCashEvidence()
    {
        // GIVEN a genuine credit and refund, and a reader with accounting report authority.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var history = prepared.History;
        using var client = context.Bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var journal = await context.Bills.ScalarAsync<Guid>($"""
            SELECT j.Id FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
            WHERE s.SourceKind='SupplierRefund' AND s.SourceId='{history.Refund}' AND s.EventKind='Refund'
            """);

        // WHEN reading the item and the real cash journal THEN each explains its own retained source.
        var item = await Read(client, $"/supplier-open-items/{history.OriginalCredit}");
        var credit = item.GetProperty("supplierSource");
        Assert.Equal("SupplierCredit", credit.GetProperty("sourceKind").GetString());
        Assert.Equal(history.OriginalCredit, credit.GetProperty("sourceId").GetGuid());
        Assert.Equal("18.00", credit.GetProperty("amount").GetString());
        Assert.Equal(history.Bill, Assert.Single(credit.GetProperty("claims").EnumerateArray()).GetProperty("billId").GetGuid());
        Assert.Contains(credit.GetProperty("corrections").EnumerateArray(),
            row => row.GetProperty("replacementSourceId").GetGuid() == history.SuccessorCredit);
        var refund = (await Read(client, $"/journals/{journal}")).GetProperty("supplierSource");
        Assert.Equal("SupplierRefund", refund.GetProperty("sourceKind").GetString());
        Assert.Equal(history.Refund, refund.GetProperty("sourceId").GetGuid());
        Assert.Equal(context.Bank, refund.GetProperty("fundingAccountId").GetGuid());
        Assert.Equal(history.Clearing, Assert.Single(refund.GetProperty("settlements").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.False(refund.TryGetProperty("evidenceJson", out _));
    }

    [Fact]
    public async Task CreditAndCashWithTheSameIdentityRemainDistinct()
    {
        // GIVEN separate credit and cash namespaces that legitimately share IDs and revisions.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var credit = prepared.History.OriginalCredit;
        var revision = await context.Bills.ScalarAsync<Guid>($"SELECT RevisionId FROM Purchasing.SupplierCredits WHERE Id='{credit}'");
        var deposit = await context.Payments.CommandAsync("20", "2026-09-10");
        await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.ToString());
        var refund = await context.RefundCommandAsync([(advance, "8")]);
        refund["cashSourceId"] = credit.ToString();
        refund["cashSourceRevisionId"] = revision.ToString();
        await context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), refund);
        var journal = await context.Bills.ScalarAsync<Guid>($"""
            SELECT JournalId FROM Purchasing.SupplierCashControl('{JournalTestContext.TenantId}','{credit}')
            """);
        using var client = context.Bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN reading both owners THEN the credit's correction never becomes the refund's correction.
        var item = (await Read(client, $"/supplier-open-items/{credit}")).GetProperty("supplierSource");
        var cash = (await Read(client, $"/journals/{journal}")).GetProperty("supplierSource");
        Assert.Equal(revision, item.GetProperty("sourceRevisionId").GetGuid());
        Assert.Equal(revision, cash.GetProperty("sourceRevisionId").GetGuid());
        Assert.Equal("SupplierCredit", item.GetProperty("sourceKind").GetString());
        Assert.Equal("18.00", item.GetProperty("amount").GetString());
        Assert.Single(item.GetProperty("corrections").EnumerateArray());
        Assert.Equal("SupplierRefund", cash.GetProperty("sourceKind").GetString());
        Assert.Equal("8.00", cash.GetProperty("amount").GetString());
        Assert.Empty(cash.GetProperty("corrections").EnumerateArray());
    }

    [Fact]
    public async Task BackdatedCashCorrectionCannotEnterContinuedHistory()
    {
        // GIVEN real refund and clearing history and a cursor with a future recorded cutoff.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var history = prepared.History;
        using var client = context.Bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var route = $"/supplier-open-items/{history.OriginalCredit}/history";
        const string query = "?pageSize=1&recordedThrough=2099-01-01T00:00:00Z&postingThrough=2026-09-30";
        var first = await Read(client, route + query);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        // WHEN a correction backdated within the posting cutoff commits between these pages.
        var command = await context.CorrectionCommandAsync("Refund", history.Refund, null, "2026-09-22");
        await context.CaptureCashCorrectionPlanAsync("Refund", command);
        var corrected = await context.CorrectAsync("Refund", command);
        var group = Guid.Parse(corrected["groupId"]!.ToString());
        var continued = await Read(client, route + query + "&cursor=" + Uri.EscapeDataString(cursor!));

        // THEN the old cash activity is explained using only the original capture, while a fresh read includes the correction.
        Assert.Equal(first.GetProperty("wholeFilterTotals").ToString(), continued.GetProperty("wholeFilterTotals").ToString());
        var historical = Assert.Single(continued.GetProperty("items").EnumerateArray());
        Assert.Equal("CashSettlement", historical.GetProperty("eventKind").GetString());
        Assert.Equal(history.Refund, Assert.Single(historical.GetProperty("settlements").EnumerateArray()).GetProperty("cashSourceId").GetGuid());
        Assert.DoesNotContain(group.ToString(), continued.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var fresh = await Read(client, route + "?postingThrough=2026-09-30");
        Assert.Contains(group.ToString(), fresh.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var beforePosting = await Read(client, route + "?postingThrough=2026-09-21");
        Assert.DoesNotContain(group.ToString(), beforePosting.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> Read(HttpClient client, string route)
    {
        using var response = await client.GetAsync("/api/beta/accounting" + route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
