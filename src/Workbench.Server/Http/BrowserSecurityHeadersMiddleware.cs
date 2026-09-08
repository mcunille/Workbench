// Copyright (c) 2026 The White Stag Collection.

using System.Net;

namespace Workbench.Server.Http;

public sealed class BrowserSecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Register outside the exception handler: clearing an error response must not remove protection.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            // Use only the scheme already established by the trusted forwarded-header boundary.
            // Local HTTPS must not pin development or localhost self-host health probes to TLS.
            var host = context.Request.Host.Host.Trim('[', ']');
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
            if (context.Request.IsHttps && !local)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
