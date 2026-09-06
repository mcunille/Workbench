// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workbench.Server.Security;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class PublicEndpointTests
{
    [Theory]
    [InlineData("10.42.0.0/24", "::ffff:10.42.0.2")]
    [InlineData("fd00:42::/64", "fd00:42::2")]
    public async Task NarrowNativeNetworksAcceptTheirObservedPeers(string network, string peer)
    {
        // GIVEN a native narrow network, including IPv4 peers presented by a dual-stack listener.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownNetworks:0"] = network,
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
            services.Configure<ForwardedHeadersOptions>(options => PublicEndpointConfiguration.ConfigureProxy(configuration, options, true)))
            .Configure(app => { app.UseForwardedHeaders(); app.Run(_ => Task.CompletedTask); })).StartAsync();
        // WHEN its observed peer forwards a client address, THEN the legitimate hop is consumed.
        var context = await host.GetTestServer().SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            request.Request.Headers["X-Forwarded-For"] = "198.51.100.8";
            request.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal("198.51.100.8", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", context.Request.Scheme);
    }

    [Theory]
    [InlineData(1, "10.42.0.3")]
    [InlineData(2, "198.51.100.8")]
    public async Task ExplicitHopLimitBoundsEvenAChainOfTrustedPeers(int hops, string expectedIp)
    {
        // GIVEN two trusted proxies but an explicitly bounded number of accepted hops.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "10.42.0.2",
            ["ReverseProxy:KnownProxies:1"] = "10.42.0.3",
            ["ReverseProxy:ForwardLimit"] = hops.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
            services.Configure<ForwardedHeadersOptions>(options => PublicEndpointConfiguration.ConfigureProxy(configuration, options, true)))
            .Configure(app => { app.UseForwardedHeaders(); app.Run(_ => Task.CompletedTask); })).StartAsync();
        // WHEN the chain includes a second trusted peer, THEN trust alone cannot bypass the hop limit.
        var context = await host.GetTestServer().SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse("10.42.0.2");
            request.Request.Headers["X-Forwarded-For"] = "198.51.100.8, 10.42.0.3";
            request.Request.Headers["X-Forwarded-Proto"] = "https, https";
        });
        Assert.Equal(expectedIp, context.Connection.RemoteIpAddress!.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AnUnboundedOrExcessiveHopLimitIsRejected(int hops)
    {
        // GIVEN a trusted proxy with an unsafe hop count, WHEN configured, THEN it fails closed.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "10.42.0.2",
            ["ReverseProxy:ForwardLimit"] = hops.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        Assert.Throws<InvalidOperationException>(() => PublicEndpointConfiguration.ConfigureProxy(configuration, new(), true));
    }

    [Theory]
    [InlineData("10.42.0.2", "198.51.100.8", "https")]
    [InlineData("10.42.0.3", "10.42.0.3", "http")]
    [InlineData("127.0.0.1", "127.0.0.1", "http")]
    public async Task OnlyTheExplicitImmediateProxyCanSupplyClientMetadata(string peer, string expectedIp, string expectedScheme)
    {
        // GIVEN an exact trusted ingress peer with a one-hop boundary.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "10.42.0.2",
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
            services.Configure<ForwardedHeadersOptions>(options => PublicEndpointConfiguration.ConfigureProxy(configuration, options, true)))
            .Configure(app => { app.UseForwardedHeaders(); app.Run(_ => Task.CompletedTask); })).StartAsync();
        var server = host.GetTestServer();
        // WHEN the request includes a forged prefix and forwarded host as well as the platform's last hop,
        var context = await server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            request.Request.Host = new HostString("workbench.example");
            request.Request.Headers["X-Forwarded-For"] = "203.0.113.99, 198.51.100.8";
            request.Request.Headers["X-Forwarded-Proto"] = "http, https";
            request.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        });
        // THEN only the trusted last hop changes scheme/address and host is never forwarded.
        Assert.Equal(expectedIp, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal(expectedScheme, context.Request.Scheme);
        Assert.Equal("workbench.example", context.Request.Host.Value);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::/0")]
    [InlineData("::/64")]
    [InlineData("::ffff:10.0.0.0/104")]
    [InlineData("invalid")]
    public void BroadOrInvalidProxyNetworksAreRejected(string cidr)
    {
        // GIVEN an overbroad or malformed proxy network, WHEN configured, THEN it cannot expand trust.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownNetworks:0"] = cidr,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => PublicEndpointConfiguration.ConfigureProxy(configuration, new(), true));
    }

    [Theory]
    [InlineData("workbench.example", HttpStatusCode.OK)]
    [InlineData("attacker.example", HttpStatusCode.BadRequest)]
    public async Task HostAllowlistIsEnforcedBeforeApplicationHandlers(string host, HttpStatusCode expected)
    {
        // GIVEN a deployed host allowlist, WHEN an API request supplies a host,
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("AllowedHosts", "workbench.example"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system");
        request.Headers.Host = host;
        // THEN an unrecognized host never reaches application handlers.
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }
}
