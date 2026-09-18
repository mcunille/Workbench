// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Http;

public static class RetiredApiResponse
{
    public static Task Unsupported(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "This API route is no longer supported. Preserve unsaved changes and reload Workbench.",
            type: "about:blank", extensions: new Dictionary<string, object?>
            {
                ["code"] = "api_contract_unsupported",
            }).ExecuteAsync(context);
    }
}
