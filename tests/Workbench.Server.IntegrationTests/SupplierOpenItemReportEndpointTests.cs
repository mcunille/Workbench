// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemReportEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MoneyAndReadAuthorizationUseTheAccountingBoundary()
    {
        // GIVEN posted supplier debt and an authenticated accounting administrator.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        await context.Allocation.BillAsync("306.60");
        using var client = context.Allocation.Journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN browsing THEN money is a decimal string and responses cannot be publicly cached.
        using var response = await client.GetAsync("/api/beta/accounting/supplier-open-items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("306.60", body.RootElement.GetProperty("items")[0].GetProperty("balance").GetString());
    }

    [Fact]
    public async Task CursorCannotChangeTenantScopeOrCutoffs()
    {
        // GIVEN multiple items and a protected first-page cursor with explicit scope and future cutoff.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150");
        await context.RecordAsync(await context.CommandAsync());
        using var client = context.Allocation.Journal.Application.CreateClient(); await LoginAsync(client, AuthTestApplication.AdminEmail);
        var query = $"?pageSize=1&supplierId={context.Bills.Recognition.SupplierId}&postingThrough=2026-09-30&recordedThrough=2099-01-01T00:00:00Z";
        var first = await Body(client, "/supplier-open-items" + query);
        var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        // WHEN any bound dimension changes THEN the protected cursor is rejected.
        foreach (var changed in new[] { query.Replace("pageSize=1", "pageSize=2"), query.Replace("2026-09-30", "2026-09-29"),
            query.Replace("2099-01-01", "2098-01-01"), query + $"&billId={bill}", query.Replace(context.Bills.Recognition.SupplierId.ToString(), Guid.NewGuid().ToString()) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/beta/accounting/supplier-open-items" + changed + "&cursor=" + cursor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/beta/accounting/supplier-reconciliation" + query + "&cursor=" + cursor)).StatusCode);
        await GrantOtherReader(context);
        using var other = context.Allocation.Journal.Application.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await other.GetAsync("/api/beta/accounting/supplier-open-items" + query + "&cursor=" + cursor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/beta/accounting/supplier-open-items?pageSize=201")).StatusCode);
    }

    [Fact]
    public async Task FutureCutoffPagesFreezeNewGroupsJournalTotalsAndHistory()
    {
        // GIVEN a payment with two visible movements and more than one report item.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150"); var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, bill, "20"));
        using var client = context.Allocation.Journal.Application.CreateClient(); await LoginAsync(client, AuthTestApplication.AdminEmail);
        const string query = "?pageSize=1&recordedThrough=2099-01-01T00:00:00Z";
        var items = await Body(client, "/supplier-open-items" + query);
        var history = await Body(client, $"/supplier-open-items/{id}/history" + query);
        var reconciliation = await Body(client, "/supplier-reconciliation" + query);
        // WHEN another backdated application and a new bill commit between page requests.
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, bill, "30"));
        await context.Allocation.BillAsync("17");
        var nextItems = await Body(client, "/supplier-open-items" + query + "&cursor=" + Uri.EscapeDataString(items.GetProperty("nextCursor").GetString()!));
        var nextHistory = await Body(client, $"/supplier-open-items/{id}/history" + query + "&cursor=" + Uri.EscapeDataString(history.GetProperty("nextCursor").GetString()!));
        var controls = reconciliation.GetProperty("controls");
        var nextReconciliation = await Body(client, "/supplier-reconciliation" + query + "&cursor=" + Uri.EscapeDataString(controls.GetProperty("nextCursor").GetString()!));
        // THEN totals, old-item balances and terminal history remain at the captured two ceilings.
        Assert.Equal(items.GetProperty("wholeFilterTotals").ToString(), nextItems.GetProperty("wholeFilterTotals").ToString());
        Assert.Equal("130.00", nextItems.GetProperty("wholeFilterTotals").GetProperty("payable").GetString());
        Assert.Equal("80.00", nextItems.GetProperty("wholeFilterTotals").GetProperty("advance").GetString());
        Assert.Equal("-20.00", nextHistory.GetProperty("items")[0].GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, nextHistory.GetProperty("nextCursor").ValueKind);
        Assert.Equal(history.GetProperty("wholeFilterTotals").ToString(), nextHistory.GetProperty("wholeFilterTotals").ToString());
        Assert.Equal(controls.GetProperty("wholeFilterTotals").ToString(), nextReconciliation.GetProperty("controls").GetProperty("wholeFilterTotals").ToString());
        Assert.True(nextReconciliation.GetProperty("isComplete").GetBoolean());
        Assert.NotEqual(nextItems.GetProperty("wholeFilterTotals").ToString(), (await Body(client, "/supplier-open-items")).GetProperty("wholeFilterTotals").ToString());
    }

    [Fact]
    public async Task MissingAndForeignItemsAreIndistinguishable()
    {
        // GIVEN a known item belonging to another tenant.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync(); await GrantOtherReader(context);
        using var other = context.Allocation.Journal.Application.CreateClient(); await LoginAsync(other, "other@example.com");
        // WHEN requesting item/detail-history/filter identities THEN missing and foreign return the same 404 title.
        foreach (var suffix in new[] { "", "/history" })
        {
            using var foreign = await other.GetAsync($"/api/beta/accounting/supplier-open-items/{bill}{suffix}");
            using var missing = await other.GetAsync($"/api/beta/accounting/supplier-open-items/{Guid.NewGuid()}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(foreign.StatusCode, missing.StatusCode);
            using var a = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync()); using var b = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal(a.RootElement.GetProperty("title").GetString(), b.RootElement.GetProperty("title").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/beta/accounting/supplier-reconciliation?billId={bill}")).StatusCode);
        // AND ordinary membership does not confer accounting report access on any route.
        using var member = context.Allocation.Journal.Application.CreateClient(); await LoginAsync(member, "member@example.com");
        foreach (var route in new[] { "/supplier-open-items", $"/supplier-open-items/{bill}", $"/supplier-open-items/{bill}/history", "/supplier-reconciliation" })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/beta/accounting" + route)).StatusCode);
    }

    private static async Task<JsonElement> Body(HttpClient client, string path)
    {
        using var response = await client.GetAsync("/api/beta/accounting" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.Clone();
    }
    private static Task GrantOtherReader(SupplierPaymentTestContext context) => context.Bills.AdminAsync($"INSERT [Identity].UserRoles(TenantId,UserId,RoleId) SELECT '{JournalTestContext.OtherTenantId}','{AuthTestApplication.OtherTenantUserId}',RoleId FROM Administration.AccountingRoles WHERE TenantId='{JournalTestContext.OtherTenantId}' AND Kind='Reader'");
}
