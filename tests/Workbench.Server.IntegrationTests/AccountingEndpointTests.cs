// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.Accounting;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingEndpointTests(SqlServerFixture sqlServer)
{
    internal static async Task GrantSetup(HttpClient client)
    {
        var roles = await client.GetFromJsonAsync<JsonElement>("/api/beta/tenant/accounting-roles");
        var role = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Accounting administrator").GetProperty("id").GetGuid();
        var path = $"/api/beta/tenant/users/{AuthTestApplication.AdminUserId}/accounting-roles";
        var current = await client.GetFromJsonAsync<JsonElement>(path);
        var response = await SendAsync(client, HttpMethod.Post, path, new { requestId = Guid.NewGuid(), expectedVersion = current.GetProperty("version").GetString(), roleIds = new[] { role } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetupRequiresAccountingAuthorityAndNeverEnablesBookkeeping()
    {
        // GIVEN an authenticated tenant administrator with no accounting roles.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN visiting accounting THEN tenant administration alone is insufficient.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/beta/accounting/setup")).StatusCode);
        await GrantSetup(client);
        var initial = (await client.GetFromJsonAsync<AccountingSetupResponse>("/api/beta/accounting/setup"))!;
        Assert.False(initial.SetupComplete); Assert.False(initial.BookkeepingAvailable);
        Assert.NotEmpty(initial.MissingItems);
        // WHEN saving a valid incomplete policy draft THEN it survives readback without activating books.
        var configuration = initial.Configuration with { Policies = initial.Configuration.Policies with { Country = "CA", Region = "BC", Currency = "CAD", Scale = 2, FiscalStartMonth = 4 } };
        var request = new SaveAccountingConfigurationRequest(Guid.NewGuid(), initial.Version, configuration);
        var saved = await SendAsync(client, HttpMethod.Put, "/api/beta/accounting/setup", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var receipt = (await saved.Content.ReadFromJsonAsync<AccountingSaveResponse>())!;
        var read = (await client.GetFromJsonAsync<AccountingSetupResponse>("/api/beta/accounting/setup"))!;
        Assert.Equal("CAD", read.Configuration.Policies.Currency); Assert.Equal(receipt.SavedVersion, read.Version);
        Assert.False(read.BookkeepingAvailable);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, "/api/beta/accounting/setup", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, "/api/beta/accounting/setup", request with { RequestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Put, "/api/beta/accounting/setup", new { requestId = Guid.NewGuid(), expectedVersion = read.Version, configuration, bookkeepingAvailable = true })).StatusCode);
    }

    [Fact]
    public async Task AccountsHaveDurableReplayReservedCodesAndMappingArchiveProtection()
    {
        // GIVEN an accounting administrator creating an atomic starter chart.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client, AuthTestApplication.AdminEmail); await GrantSetup(client);
        var create = new CreateAccountingAccountsRequest(Guid.NewGuid(), AccountingCatalog.Value.StarterAccounts);
        var result = await SendAsync(client, HttpMethod.Post, "/api/beta/accounting/accounts", create);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var receipt = (await result.Content.ReadFromJsonAsync<AccountingSaveResponse>())!;
        Assert.Equal(create.Accounts.Count, receipt.AccountIds.Count);
        // WHEN retrying THEN no duplicates appear, and changed payload reuse fails.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/beta/accounting/accounts", create)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/beta/accounting/accounts", create with { Accounts = [create.Accounts[0]] })).StatusCode);
        var page = (await client.GetFromJsonAsync<AccountingAccountPage>("/api/beta/accounting/accounts"))!;
        Assert.Equal(create.Accounts.Count, page.Items.Count);
        var payable = page.Items.Single(a => a.Purpose == "SupplierPayable");
        var setup = (await client.GetFromJsonAsync<AccountingSetupResponse>("/api/beta/accounting/setup"))!;
        var configuration = setup.Configuration with { Mappings = [new("SupplierPayable", payable.Id)] };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, "/api/beta/accounting/setup", new SaveAccountingConfigurationRequest(Guid.NewGuid(), setup.Version, configuration))).StatusCode);
        // THEN an active control mapping prevents archiving its account.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, $"/api/beta/accounting/accounts/{payable.Id}/archive", new ArchiveAccountingAccountRequest(Guid.NewGuid(), payable.Version, true))).StatusCode);
        var expense = page.Items.Single(a => a.Code == "6000");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, $"/api/beta/accounting/accounts/{expense.Id}/archive", new ArchiveAccountingAccountRequest(Guid.NewGuid(), expense.Version, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/beta/accounting/accounts", new CreateAccountingAccountsRequest(Guid.NewGuid(), [new("6000", "Replacement", "Expense", "General", null)]))).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<AccountingAccountPage>("/api/beta/accounting/accounts"))!.Items, a => a.Id == expense.Id);
        Assert.Contains((await client.GetFromJsonAsync<AccountingAccountPage>("/api/beta/accounting/accounts?includeArchived=true"))!.Items, a => a.Id == expense.Id && a.IsArchived);
        var archived = (await client.GetFromJsonAsync<AccountingAccountPage>("/api/beta/accounting/accounts?includeArchived=true"))!.Items.Single(a => a.Id == expense.Id);
        // AND omitting the archive decision cannot silently restore the account.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, $"/api/beta/accounting/accounts/{expense.Id}/archive",
            new { requestId = Guid.NewGuid(), expectedVersion = archived.Version })).StatusCode);
    }
}
