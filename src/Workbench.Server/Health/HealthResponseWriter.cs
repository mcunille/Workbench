using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Workbench.Server.Health;

internal static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        return context.Response.WriteAsJsonAsync(
            new HealthResponse(report.Status.ToString()),
            cancellationToken: context.RequestAborted);
    }

    private sealed record HealthResponse(string Status);
}
