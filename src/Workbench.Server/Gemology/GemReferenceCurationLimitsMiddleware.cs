// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Http.Features;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceCurationWriteMetadata;

public sealed class GemReferenceCurationLimitsMiddleware(RequestDelegate next)
{
    public const int MaximumRequestBytes = 1024 * 1024;
    public async Task InvokeAsync(HttpContext context)
    {
        var tenantWrite = context.GetEndpoint()?.Metadata.GetMetadata<GemReferenceTenantWriteMetadata>() is not null;
        if (tenantWrite || context.GetEndpoint()?.Metadata.GetMetadata<GemReferenceTenantReadMetadata>() is not null)
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                return Task.CompletedTask;
            });
        if (!tenantWrite && context.GetEndpoint()?.Metadata.GetMetadata<GemReferenceCurationWriteMetadata>() is null)
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "private, no-store";
        if (!context.Request.Headers.ContainsKey("X-CSRF-TOKEN"))
        {
            await Results.Problem(statusCode: 400, title: "Antiforgery validation failed.").ExecuteAsync(context);
            return;
        }
        if (context.Request.ContentLength > MaximumRequestBytes)
        {
            await TooLargeAsync(context, tenantWrite);
            return;
        }
        var limits = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limits is { IsReadOnly: false }) limits.MaxRequestBodySize = MaximumRequestBytes;
        // Bound chunked bodies too, without spilling draft content into a temporary file.
        context.Request.EnableBuffering(MaximumRequestBytes + 1, MaximumRequestBytes);
        try { await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await TooLargeAsync(context, tenantWrite);
            return;
        }
        catch (IOException)
        {
            await TooLargeAsync(context, tenantWrite);
            return;
        }
        context.Request.Body.Position = 0;
        await next(context);
    }
    private static Task TooLargeAsync(HttpContext context, bool tenantWrite) => Results.Problem(statusCode: 413,
        title: tenantWrite ? "Use a reference request of at most 1 MiB." : "Use a curation request of at most 1 MiB.", extensions: new Dictionary<string, object?> { ["code"] = "request_too_large" }).ExecuteAsync(context);
}
