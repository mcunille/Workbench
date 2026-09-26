// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalCorrectionReportTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DetailShowsBothRolesWhenReplacementIsCorrectedAgain()
    {
        // GIVEN an original September entry corrected with an October replacement, then corrected again.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"),
            postingDate: new DateTime(2026, 9, 15));
        await controls.CloseAsync(new DateOnly(2026, 9, 1));
        var first = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        var second = await controls.CorrectAsync(first.ReplacementJournalId!.Value, new DateOnly(2026, 10, 2), "270");
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN the replacement detail is read THEN it retains both relationships and immutable evidence.
        using var response = await client.GetAsync($"/api/beta/accounting/journals/{first.ReplacementJournalId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var groups = body.RootElement.GetProperty("corrections").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Contains(groups, group => group.GetProperty("correctionId").GetGuid() == first.CorrectionId &&
            group.GetProperty("role").GetString() == "Replacement" &&
            group.GetProperty("originalJournalId").GetGuid() == original.JournalId &&
            group.GetProperty("replacementJournalId").GetGuid() == first.ReplacementJournalId &&
            group.GetProperty("snapshotJson").GetString()!.Contains("originalSourceRevision") &&
            group.GetProperty("snapshotSha256").GetString()!.Length == 64);
        Assert.Contains(groups, group => group.GetProperty("correctionId").GetGuid() == second.CorrectionId &&
            group.GetProperty("role").GetString() == "Original" &&
            group.GetProperty("reversalJournalId").GetGuid() == second.ReversalJournalId);
        // AND list headers carry no later relationship metadata.
        using var list = await client.GetAsync("/api/beta/accounting/journals");
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.All(page.RootElement.GetProperty("items").EnumerateArray(),
            item => Assert.False(item.TryGetProperty("corrections", out _)));
        // AND another tenant cannot use the original, reversal or replacement ID to read this history.
        await using (var admin = new SqlConnection(controls.Journal.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                    SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                    WHERE TenantId=@tenant AND Kind='Reader'
                """, admin);
            grant.Parameters.AddWithValue("@tenant", JournalTestContext.OtherTenantId);
            grant.Parameters.AddWithValue("@user", AuthTestApplication.OtherTenantUserId);
            await grant.ExecuteNonQueryAsync();
        }
        using var other = controls.Journal.Application.CreateClient();
        await LoginAsync(other, "other@example.com");
        foreach (var id in new[] { original.JournalId, first.ReversalJournalId, first.ReplacementJournalId!.Value })
            Assert.Equal(HttpStatusCode.NotFound,
                (await other.GetAsync($"/api/beta/accounting/journals/{id}")).StatusCode);
    }

    [Fact]
    public async Task SeptemberClosureAndRecordedCutoffPreserveOriginalFinancialView()
    {
        // GIVEN September's 300 entry, then its October reversal and 280 replacement.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"),
            postingDate: new DateTime(2026, 9, 15));
        await controls.CloseAsync(new DateOnly(2026, 9, 1));
        var correction = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN reporting through September THEN the closed month's balance remains 300.
        using (var september = await client.GetAsync("/api/beta/accounting/trial-balance?postingThrough=2026-09-30"))
        {
            Assert.Equal(HttpStatusCode.OK, september.StatusCode);
            using var body = JsonDocument.Parse(await september.Content.ReadAsStringAsync());
            Assert.Equal("300.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Contains(body.RootElement.GetProperty("items").EnumerateArray(),
                row => row.GetProperty("accountId").GetGuid() == controls.Journal.DebitAccountId &&
                    row.GetProperty("debitMinusCredit").GetString() == "300.00");
        }
        // WHEN cutoff is immediately before the shared correction timestamp THEN no half correction appears.
        var before = Uri.EscapeDataString(correction.RecordedAtUtc.AddTicks(-1).ToString("O"));
        using (var response = await client.GetAsync($"/api/beta/accounting/journals?recordedThrough={before}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("300.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.False(body.RootElement.GetProperty("items")[0].TryGetProperty("corrections", out _));
        }
        // WHEN cutoff equals that timestamp THEN both October entries and all activity appear.
        var at = Uri.EscapeDataString(correction.RecordedAtUtc.ToString("O"));
        using (var response = await client.GetAsync($"/api/beta/accounting/journals?recordedThrough={at}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(3, body.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal("880.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        }
        using (var october = await client.GetAsync("/api/beta/accounting/trial-balance?postingThrough=2026-10-31"))
        {
            using var body = JsonDocument.Parse(await october.Content.ReadAsStringAsync());
            Assert.Equal("880.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Contains(body.RootElement.GetProperty("items").EnumerateArray(),
                row => row.GetProperty("accountId").GetGuid() == controls.Journal.DebitAccountId &&
                    row.GetProperty("debitMinusCredit").GetString() == "280.00");
        }
        // AND current detail shows the later relationship without altering the header.
        using var detail = await client.GetAsync($"/api/beta/accounting/journals/{original.JournalId}");
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Single(detailBody.RootElement.GetProperty("corrections").EnumerateArray());
        Assert.False(detailBody.RootElement.GetProperty("header").TryGetProperty("corrections", out _));
    }
}
