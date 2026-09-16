// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Server.Identity;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SessionCookieConfigurationTests
{
    [Fact]
    public void CookieTicketContainsOnlyOpaqueTokenAndFormatVersion()
    {
        // GIVEN an opaque session token WHEN its cookie principal is constructed.
        var principal = SessionCookieHandler.CreateCookiePrincipal("opaque-random-token");

        // THEN only the opaque token and its format version are carried.
        var claims = principal.Claims.OrderBy(claim => claim.Type).ToArray();
        Assert.Equal(2, claims.Length);
        Assert.Equal(SessionCookieHandler.FormatVersionClaimType, claims[0].Type);
        Assert.Equal(SessionCookieHandler.CurrentFormatVersion, claims[0].Value);
        Assert.Equal(SessionCookieHandler.SessionTokenClaimType, claims[1].Type);
        Assert.Equal("opaque-random-token", claims[1].Value);
    }

    [Fact]
    public async Task DevelopmentCookieIsHttpOnlySameSiteAndNotSliding()
    {
        // GIVEN a development host WHEN its configured cookie options are resolved.
        await using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SessionCookieHandler.Scheme);

        // THEN its browser protections and fixed twelve-hour lifetime need no database.
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.False(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
    }
}
