// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Purchasing;

public sealed class DraftOrderCacheMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/purchase-order-drafts"))
            context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "private, no-store"; return Task.CompletedTask; });
        await next(context);
    }
}

public sealed class DraftOrderRequestMiddleware(RequestDelegate next)
{
    public const int MaximumBodyBytes = 4 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/purchase-order-drafts") ||
            context.Request.Method is not ("POST" or "PUT")) { await next(context); return; }
        if (context.Request.ContentLength > MaximumBodyBytes) { await TooLarge(context); return; }
        // Bound chunked requests as well as Content-Length. Keep private bodies in memory only;
        // bind from the bounded buffer so framework JSON binding cannot read an unbounded stream.
        var original = context.Request.Body;
        await using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumBodyBytes + 1 - bounded.Length)), context.RequestAborted);
            if (read == 0) break;
            await bounded.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            if (bounded.Length > MaximumBodyBytes) { await TooLarge(context); return; }
        }
        bounded.Position = 0;
        context.Request.Body = bounded;
        try { await next(context); }
        finally { context.Request.Body = original; }
    }

    private static Task TooLarge(HttpContext context) => Results.Problem(statusCode: 413,
        title: "The draft request exceeds the 4 MiB limit.", type: "about:blank").ExecuteAsync(context);
}
