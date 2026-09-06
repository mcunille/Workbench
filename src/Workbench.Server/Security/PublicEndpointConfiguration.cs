// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Workbench.Server.Security;

public static class PublicEndpointConfiguration
{
    public static void Validate(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["PublicOrigin"], UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Production requires a canonical HTTPS public origin.");
        }
        var hosts = configuration["AllowedHosts"]?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (hosts.Length == 0 || hosts.Any(host => host.Contains('*') || Uri.CheckHostName(host) == UriHostNameType.Unknown) ||
            !hosts.Contains(origin.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Production requires explicit allowed hosts including the canonical public origin.");
        }
        var messageOrigin = configuration["Smtp:PublicOrigin"];
        if (!string.IsNullOrEmpty(messageOrigin) &&
            (!Uri.TryCreate(messageOrigin, UriKind.Absolute, out var smtp) || smtp != origin))
        {
            throw new InvalidOperationException("SMTP public origin must match the canonical public origin.");
        }
    }

    public static void ConfigureProxy(IConfiguration configuration, ForwardedHeadersOptions options, bool required)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardedHeaders = ForwardedHeaders.None;
        options.ForwardLimit = 1;
        var proxies = configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        var legacy = ProductionSecurityConfigurationValidator.GetKnownProxy(configuration);
        if (!string.IsNullOrEmpty(legacy))
        {
            proxies = [.. proxies, legacy];
        }
        foreach (var value in proxies)
        {
            if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException("A trusted proxy must be an explicit IP address.");
            }
            options.KnownProxies.Add(address);
        }
        foreach (var value in configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network) ||
                network.PrefixLength < (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 24 : 64) ||
                IncludesMappedAddresses(network))
            {
                throw new InvalidOperationException("A trusted proxy network must be narrow and must not include IPv4-mapped IPv6 space; configure IPv4 ranges directly.");
            }
            options.KnownIPNetworks.Add(network);
        }
        if (options.KnownProxies.Count + options.KnownIPNetworks.Count == 0)
        {
            if (required)
            {
                throw new InvalidOperationException("Production requires a valid trusted proxy configuration.");
            }
            return;
        }
        var limit = configuration.GetValue("ReverseProxy:ForwardLimit", 1);
        if (limit is < 1 or > 3)
        {
            throw new InvalidOperationException("Trusted proxy hop count must be between one and three.");
        }
        options.ForwardLimit = limit;
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    private static bool IncludesMappedAddresses(System.Net.IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }
        var address = network.BaseAddress.GetAddressBytes();
        var mapped = IPAddress.Parse("::ffff:192.0.2.1").GetAddressBytes();
        // Compare the network prefix to mapped IPv4 space (::ffff:0:0/96) directly;
        // IPNetwork.Contains can normalize mapped addresses and obscure this overlap.
        for (var bit = 0; bit < Math.Min(network.PrefixLength, 96); bit++)
        {
            var mask = 1 << (7 - bit % 8);
            if ((address[bit / 8] & mask) != (mapped[bit / 8] & mask))
            {
                return false;
            }
        }
        return true;
    }
}
