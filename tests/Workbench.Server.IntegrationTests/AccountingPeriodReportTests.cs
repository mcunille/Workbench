// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingPeriodReportTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ReadingOpenMonthsDoesNotMaterializePeriods()
    {
        // GIVEN configured books with no postings.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN an authorized reader asks for two months.
        using var response = await client.GetAsync("/api/beta/accounting/periods?from=2026-09-01&through=2026-10-01");
        // THEN the response shows both open months without creating durable rows.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(body.RootElement.GetProperty("items").EnumerateArray(),
            row => Assert.Equal("Open", row.GetProperty("state").GetString()));
        Assert.Equal(0, await controls.Journal.CountAsync("Periods"));
    }

    [Fact]
    public async Task ClosedAndPreStartMonthsUseTheStoredCalendar()
    {
        // GIVEN a partial first month and a later closed month.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer,
            fiscalStartMonth: 4, startDate: "2026-09-15");
        var closure = await controls.CloseAsync(new DateOnly(2026, 10, 1));
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN reading the requested inclusive range THEN entirely pre-start months disappear.
        using var response = await client.GetAsync("/api/beta/accounting/periods?from=2026-08-01&through=2026-10-01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("2026-09-01", rows[0].GetProperty("periodStart").GetString());
        Assert.Equal("2026-09-30", rows[0].GetProperty("periodEnd").GetString());
        Assert.Equal("2026-04-01", rows[0].GetProperty("fiscalYearStart").GetString());
        Assert.Equal("Open", rows[0].GetProperty("state").GetString());
        Assert.Equal("Closed", rows[1].GetProperty("state").GetString());
        Assert.Equal(closure.ClosureId, rows[1].GetProperty("closureId").GetGuid());
        Assert.Equal(closure.RecordedAtUtc, rows[1].GetProperty("closedAtUtc").GetDateTimeOffset());
        Assert.Equal(1, await controls.Journal.CountAsync("Periods"));
        // AND a range wholly before the accounting start has no rows.
        using var earlier = await client.GetAsync("/api/beta/accounting/periods?from=2026-08-01&through=2026-08-01");
        using var earlierBody = JsonDocument.Parse(await earlier.Content.ReadAsStringAsync());
        Assert.Empty(earlierBody.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ExplicitRangeRejectsMalformedAndOversizedRequests()
    {
        // GIVEN a configured reader and an untouched calendar.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN requesting 120 months and the final representable month THEN both are bounded reads.
        using (var response = await client.GetAsync("/api/beta/accounting/periods?from=2026-01-01&through=2035-12-01"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(120, body.RootElement.GetProperty("items").GetArrayLength());
        }
        using (var response = await client.GetAsync("/api/beta/accounting/periods?from=9999-12-01&through=9999-12-01"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("9999-12-31", body.RootElement.GetProperty("items")[0].GetProperty("periodEnd").GetString());
        }
        // THEN invalid fields, duplicate fields and the 121-month boundary return 400.
        foreach (var query in new[] { "", "from=2026-01-01", "from=2026-01-02&through=2026-02-01",
            "from=2026-02-01&through=2026-01-01", "from=2026-01-01&through=2036-01-01",
            "from=2026-01-01&from=2026-02-01&through=2026-03-01",
            "from=2026-01-01&through=2026-02-01&extra=1", "from=9999-13-01&through=9999-12-01" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"/api/beta/accounting/periods?{query}")).StatusCode);
        Assert.Equal(0, await controls.Journal.CountAsync("Periods"));
    }

    [Fact]
    public async Task PeriodReadNeedsReportPermissionAndConfiguration()
    {
        // GIVEN an authenticated tenant with no accounting report role or calendar.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        const string path = "/api/beta/accounting/periods?from=2026-09-01&through=2026-09-01";
        // WHEN the ungranted user reads THEN authority is enforced before configuration status.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await AccountingEndpointTests.GrantSetup(client);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path)).StatusCode);
        // AND a reader-only principal has report authority without setup permission.
        await using var connection = new SqlConnection(app.AdminConnectionString);
        await connection.OpenAsync();
        await using var grant = new SqlCommand("""
            INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                WHERE TenantId=@tenant AND Kind='Reader'
            """, connection);
        grant.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        grant.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
        await grant.ExecuteNonQueryAsync();
        using var reader = app.CreateClient();
        await LoginAsync(reader, "member@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await reader.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/beta/accounting/setup")).StatusCode);
    }

    [Fact]
    public async Task ReaderWithoutSetupPermissionCanReadConfiguredPeriods()
    {
        // GIVEN configured books and a member granted only the accounting Reader role.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        await using var connection = new SqlConnection(controls.Journal.Application.AdminConnectionString);
        await connection.OpenAsync();
        await using var grant = new SqlCommand("""
            INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                WHERE TenantId=@tenant AND Kind='Reader'
            """, connection);
        grant.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        grant.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
        await grant.ExecuteNonQueryAsync();
        using var client = controls.Journal.Application.CreateClient();
        await LoginAsync(client, "member@example.com");
        // WHEN reading periods THEN report authority suffices without setup authority.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/beta/accounting/periods?from=2026-09-01&through=2026-09-01")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/beta/accounting/setup")).StatusCode);
    }
}
