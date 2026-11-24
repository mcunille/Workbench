// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalReportContinuationTests(SqlServerFixture server)
{
    [Fact]
    public async Task AccountAndTrialContinuationsKeepTheirOwnOrderingAndFrozenTotals()
    {
        // GIVEN real postings whose posting-date order differs from their insertion order.
        await using var journal = await JournalTestContext.OpenAsync(server);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync();
        var later = await journal.PostAsync(await journal.CreateSourceAsync(amount: "7.00"), postingDate: new DateTime(2026, 2, 1));
        var earlier = await journal.PostAsync(await journal.CreateSourceAsync(amount: "3.00"), postingDate: new DateTime(2026, 1, 1));
        using var client = journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        foreach (var route in new[] { $"accounts/{journal.DebitAccountId}/journal", "trial-balance" })
        {
            // WHEN traversing each report through its own protected continuation.
            using var firstResponse = await client.GetAsync($"/api/beta/accounting/{route}?pageSize=1");
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrEmpty(cursor));
            using var nextResponse = await client.GetAsync($"/api/beta/accounting/{route}?pageSize=1&cursor={Uri.EscapeDataString(cursor!)}");
            Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
            using var next = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
            // THEN the two pages cover different actual rows and retain the complete frozen totals.
            Assert.Null(next.RootElement.GetProperty("nextCursor").GetString());
            Assert.Equal(first.RootElement.GetProperty("recordedThrough").GetString(), next.RootElement.GetProperty("recordedThrough").GetString());
            foreach (var page in new[] { first, next })
                Assert.Equal("10.00", page.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            var firstRow = Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
            var nextRow = Assert.Single(next.RootElement.GetProperty("items").EnumerateArray());
            if (route == "trial-balance")
                Assert.True(new[] { journal.DebitAccountId, journal.CreditAccountId }.ToHashSet().SetEquals(
                    new[] { firstRow.GetProperty("accountId").GetGuid(), nextRow.GetProperty("accountId").GetGuid() }));
            else
            {
                Assert.Equal(earlier.JournalId, firstRow.GetProperty("journalId").GetGuid());
                Assert.Equal(later.JournalId, nextRow.GetProperty("journalId").GetGuid());
                Assert.Equal("3.00", first.RootElement.GetProperty("pageTotals").GetProperty("debit").GetString());
                Assert.Equal("7.00", next.RootElement.GetProperty("pageTotals").GetProperty("debit").GetString());
            }
        }
    }
}
