// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Workbench.Server.Authorization;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Workbench.Server.ServiceAdministration;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminAuthEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string Prefix = "/api/beta/service-admin/auth";
    private AuthTestApplication _application = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        await _application.ProvisionServiceAdminAsync();
        _client = _application.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _application.DisposeAsync();
    }

    [Fact]
    public async Task LoginMeAndLogoutUseOnlyTheDedicatedIdentity()
    {
        // GIVEN an enabled service admin without a tenant sign-in
        var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal("", await login.Content.ReadAsStringAsync());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(".Workbench.ServiceAdmin=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        // WHEN reading the signed-in identity
        var me = await _client.GetAsync(Prefix + "/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var identity = await me.Content.ReadFromJsonAsync<JsonElement>();
        // THEN only account ID and email are exposed, without tokens or tenant fields
        Assert.Equal(new[] { "accountId", "email" }, identity.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(AuthTestApplication.ServiceAdminId, identity.GetProperty("accountId").GetGuid());
        Assert.Equal(AuthTestApplication.AdminEmail, identity.GetProperty("email").GetString());
        // WHEN signing out with current admin CSRF identity
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Prefix, "/logout", null)).StatusCode);
        // THEN the browser loses its authority
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(Prefix + "/me")).StatusCode);
    }

    [Theory]
    [InlineData("missing@example.com", "wrong")]
    [InlineData(AuthTestApplication.AdminEmail, "wrong")]
    [InlineData(AuthTestApplication.AdminEmail, AuthTestApplication.AdminPassword)]
    [InlineData("member@example.com", AuthTestApplication.AdminPassword)]
    [InlineData("", "")]
    public async Task CredentialFailuresRemainGeneric(string email, string password)
    {
        // GIVEN unknown, wrong, tenant-only or out-of-bounds credentials
        // WHEN attempting admin sign-in
        var response = await PostAsync(Prefix, "/login", new { email, password });
        // THEN no account existence is disclosed
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication failed.", body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("DisableServiceAdmin")]
    [InlineData("RevokeServiceAdminSessions")]
    [InlineData("ResetServiceAdminPassword")]
    public async Task MaintenanceEndsAuthorityOnNextRequest(string operation)
    {
        // GIVEN a signed-in service admin
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        // WHEN an operator changes the durable account authority
        await _application.MaintainServiceAdminAsync(operation);
        // THEN the old cookie cannot authenticate any further request
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(Prefix + "/me")).StatusCode);
        if (operation == "DisableServiceAdmin")
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync()).StatusCode);
    }

    [Fact]
    public async Task MutationsRequireCsrfAndMalformedBindingReturnsBadRequest()
    {
        // GIVEN no CSRF bootstrap
        // WHEN posting valid credentials
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(Prefix + "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword })).StatusCode);
        // GIVEN a valid CSRF bootstrap with malformed JSON
        var token = await BootstrapAsync(Prefix);
        using var malformed = new HttpRequestMessage(HttpMethod.Post, Prefix + "/login")
        { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        malformed.Headers.Add("X-CSRF-TOKEN", token);
        // WHEN binding the request THEN the contract is 400
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(malformed)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        // WHEN signing out without CSRF THEN authority is retained
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(Prefix + "/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Prefix + "/me")).StatusCode);
    }

    [Fact]
    public async Task BothCookiesSelectIndependentIdentityAndCsrfIdentity()
    {
        // GIVEN independently signed-in accounts with the same email and different passwords
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        // WHEN each authority reads its own current identity
        var admin = await _client.GetFromJsonAsync<JsonElement>(Prefix + "/me");
        var tenant = await _client.GetFromJsonAsync<JsonElement>("/api/beta/auth/me");
        // THEN the independent account IDs remain intact
        Assert.Equal(AuthTestApplication.ServiceAdminId, admin.GetProperty("accountId").GetGuid());
        Assert.Equal(2, admin.EnumerateObject().Count());
        Assert.Equal(AuthTestApplication.AdminUserId, tenant.GetProperty("userId").GetGuid());
        Assert.Equal("Tenant A", tenant.GetProperty("tenantName").GetString());
        // GIVEN CSRF issued under the tenant identity
        var tenantToken = await BootstrapAsync("/api/beta/auth");
        using var cross = new HttpRequestMessage(HttpMethod.Post, Prefix + "/logout");
        cross.Headers.Add("X-CSRF-TOKEN", tenantToken);
        // WHEN presented to the admin identity THEN the identity binding rejects it
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(cross)).StatusCode);
        // WHEN admin CSRF signs out THEN tenant sign-in still works
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Prefix, "/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/beta/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AdminCredentialsCannotSignInAsTenant()
    {
        // GIVEN an admin password differing from the same-email tenant password
        // WHEN it is submitted to tenant login THEN it grants no tenant session
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword })).StatusCode);
    }

    [Theory]
    [InlineData("/api/beta/auth/me")]
    [InlineData("/api/beta/items")]
    [InlineData("/api/beta/items/11111111-1111-1111-1111-111111111111?tenantId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("/api/beta/purchase-orders")]
    [InlineData("/api/beta/tenant/users")]
    [InlineData("/api/beta/accounting/catalog")]
    public async Task AdminCookieCannotEnterTenantApis(string path)
    {
        // GIVEN only an admin cookie, including requests substituting known tenant IDs
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        // WHEN requesting a tenant API THEN authorization rejects it before tenant handlers
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CookiesCannotBeTransferredToTheOppositeScheme(bool adminSource)
    {
        // GIVEN a valid cookie for exactly one scheme
        var login = adminSource ? await LoginAsync() : await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var other = _application.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var value = cookie[(cookie.IndexOf('=') + 1)..];
        var target = adminSource ? "/api/beta/auth/me" : Prefix + "/me";
        // WHEN carrying its original name or substituting the opposite cookie name
        foreach (var name in new[] { cookie[..cookie.IndexOf('=')], adminSource ? ".Workbench.Session" : ".Workbench.ServiceAdmin" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Add("Cookie", name + "=" + value);
            // THEN neither its name nor encrypted ticket grants the opposite authority
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task EveryProtectedApiPinsItsOwnAuthority()
    {
        // GIVEN the application's real route graph and effective authorization policies
        await _client.GetAsync(Prefix + "/antiforgery");
        var routes = _application.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/beta/", StringComparison.Ordinal) == true).ToArray();
        Assert.Contains(routes, route => route.RoutePattern.RawText!.StartsWith("/api/beta/service-admin", StringComparison.Ordinal));
        var provider = _application.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        // WHEN inspecting every protected API THEN its policy pins exactly its own authority
        foreach (var route in routes.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null))
        {
            // These two intentionally public routes carry no application data or identity authority.
            if (route.RoutePattern.RawText is "/api/beta/system" or "/api/beta/{**path}") continue;
            var authorization = route.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.True(authorization.Count > 0, $"API route {route.RoutePattern.RawText} must declare authorization or anonymous admission.");
            var isAdmin = route.RoutePattern.RawText!.StartsWith("/api/beta/service-admin", StringComparison.Ordinal);
            if (isAdmin) Assert.Contains(authorization, p => p.Policy == "ServiceAdmin");
            var policy = await AuthorizationPolicy.CombineAsync(provider, authorization);
            Assert.NotNull(policy);
            Assert.Equal(new[] { isAdmin ? "WorkbenchServiceAdmin" : "WorkbenchSession" }, policy.AuthenticationSchemes);
        }
    }

    [Fact]
    public async Task ColdHostUsesExistingKeysAndAdminRequestsCannotResolveTenantAuthority()
    {
        // GIVEN both independent cookies and a fresh host with an empty in-memory key cache
        var tenantLogin = await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword });
        var adminLogin = await LoginAsync();
        Assert.Equal(HttpStatusCode.NoContent, adminLogin.StatusCode);
        var cookies = new[] { tenantLogin, adminLogin }.Select(r => Assert.Single(r.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        var observation = new AuthorityObservation();
        await using var coldHost = _application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(observation)));
        using var client = coldHost.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, Prefix + "/me");
        request.Headers.Add("Cookie", string.Join("; ", cookies));
        // Removing tenant session procedure access makes accidental tenant authentication observable,
        // even if later admin authorization would replace the final principal.
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using (var denyTenant = new SqlCommand("REVOKE EXECUTE ON [Identity].[ResolveSession] TO [workbench_web]", connection))
            await denyTenant.ExecuteNonQueryAsync();
        // WHEN its very first HTTP request authenticates the admin cookie
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        // THEN existing SQL keys preserve the cookie, the request principal has no tenant authority,
        // AND tenant context, actor and data context cannot be constructed on that request
        Assert.NotNull(observation.Principal);
        Assert.Equal("WorkbenchServiceAdmin", Assert.Single(observation.Principal.Identities).AuthenticationType);
        Assert.DoesNotContain(observation.Principal.Claims, c => c.Type == SessionCookieHandler.TenantIdClaimType || c.Type == SessionCookieHandler.PermissionClaimType);
        Assert.Equal(3, observation.DeniedTenantServices);
        await using (var restoreTenant = new SqlCommand("GRANT EXECUTE ON [Identity].[ResolveSession] TO [workbench_web]", connection))
            await restoreTenant.ExecuteNonQueryAsync();
        // AND the same cold key storage still recognizes the independently issued tenant cookie
        using var tenant = new HttpRequestMessage(HttpMethod.Get, "/api/beta/auth/me");
        tenant.Headers.Add("Cookie", string.Join("; ", cookies));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(tenant)).StatusCode);
        Assert.NotNull(observation.TenantPrincipal);
        Assert.Equal("WorkbenchSession", Assert.Single(observation.TenantPrincipal.Identities).AuthenticationType);
        Assert.DoesNotContain(observation.TenantPrincipal.Claims, claim => claim.Type.StartsWith("workbench/service_admin/", StringComparison.Ordinal));
    }

    private sealed class AuthorityObservation : IStartupFilter
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public ClaimsPrincipal? TenantPrincipal { get; private set; }
        public int DeniedTenantServices { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                await continuation();
                if (context.Request.Path == "/api/beta/auth/me") TenantPrincipal = context.User;
                if (!context.Request.Path.StartsWithSegments("/api/beta/service-admin")) return;
                Principal = context.User;
                foreach (var service in new[] { typeof(TenantContext), typeof(RequestActor), typeof(WorkbenchDbContext) })
                {
                    try { context.RequestServices.GetRequiredService(service); }
                    catch (InvalidOperationException) { DeniedTenantServices++; }
                }
            });
            next(app);
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpaqueSessionTokensCannotSubstituteAcrossDurableStores(bool adminSource)
    {
        // GIVEN a legitimate cookie containing an opaque token from one authority
        var login = adminSource ? await LoginAsync() : await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var options = _application.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var source = options.Get(adminSource ? ServiceAdminCookieHandler.Scheme : SessionCookieHandler.Scheme);
        var ticket = source.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie[(cookie.IndexOf('=') + 1)..]))!;
        var token = ticket.Principal.FindFirst(adminSource ? ServiceAdminCookieHandler.SessionTokenClaimType : SessionCookieHandler.SessionTokenClaimType)!.Value;
        var scheme = adminSource ? SessionCookieHandler.Scheme : ServiceAdminCookieHandler.Scheme;
        var target = options.Get(scheme);
        // WHEN a ticket is correctly protected for the opposite scheme but carries the foreign opaque token
        var principal = adminSource ? SessionCookieHandler.CreateCookiePrincipal(token) : ServiceAdminCookieHandler.CreateCookiePrincipal(token);
        var substituted = target.TicketDataFormat.Protect(new AuthenticationTicket(principal, ticket.Properties, scheme));
        using var other = _application.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, adminSource ? "/api/beta/auth/me" : Prefix + "/me");
        request.Headers.Add("Cookie", target.Cookie.Name + "=" + substituted);
        // THEN durable session lookup independently rejects it, beyond cookie purpose protection
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginLimitsDoNotConsumeTheOtherAuthoritysAccountOrNetworkBudget(bool exhaustAdmin)
    {
        // GIVEN five unsuccessful attempts exhausting one authority for the same email and network
        var exhaustedPrefix = exhaustAdmin ? Prefix : "/api/beta/auth";
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(exhaustedPrefix, "/login",
                new { email = AuthTestApplication.AdminEmail, password = "wrong" })).StatusCode);
        // WHEN independently signing in to the other authority THEN its separate budget permits login
        var response = exhaustAdmin ? await PostAsync("/api/beta/auth", "/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }) : await LoginAsync();
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // AND the exhausted authority still denies even correct credentials
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(exhaustedPrefix, "/login",
            new { email = AuthTestApplication.AdminEmail, password = exhaustAdmin ? AuthTestApplication.ServiceAdminPassword : AuthTestApplication.AdminPassword })).StatusCode);
    }

    private Task<HttpResponseMessage> LoginAsync() => PostAsync(Prefix, "/login",
        new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword });

    private async Task<string> BootstrapAsync(string prefix)
    {
        var response = await _client.GetAsync(prefix + "/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.RequestToken;
    }

    private async Task<HttpResponseMessage> PostAsync(string prefix, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix + path);
        request.Headers.Add("X-CSRF-TOKEN", await BootstrapAsync(prefix));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
