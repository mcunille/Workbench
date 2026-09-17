// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Http;

public sealed class BetaApiContractMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Workbench-Api-Revision";
    public const string Revision = "beta-1";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/beta"))
        {
            await next(context);
            return;
        }

        context.Response.Headers[HeaderName] = Revision;
        var supplied = context.Request.Headers[HeaderName];
        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
        // Read-only bootstrap and native image/download requests need no custom header.
        // Never let a stale browser discover a new revision and automatically write with it.
        if ((!read || supplied.Count > 0) && (supplied.Count != 1 || supplied[0] != Revision))
        {
            await Unsupported(context);
            return;
        }

        await next(context);
    }

    public static Task Unsupported(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers[HeaderName] = Revision;
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "This API contract is no longer supported. Preserve unsaved changes and reload Workbench.",
            type: "about:blank", extensions: new Dictionary<string, object?>
            {
                ["code"] = "api_contract_unsupported",
                ["apiRevision"] = Revision,
            }).ExecuteAsync(context);
    }
}
