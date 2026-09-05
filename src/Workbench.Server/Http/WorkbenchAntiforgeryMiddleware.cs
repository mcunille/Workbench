// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Antiforgery;

namespace Workbench.Server.Http;

public sealed class WorkbenchAntiforgeryMetadata
{
    public static WorkbenchAntiforgeryMetadata Instance { get; } = new();

    private WorkbenchAntiforgeryMetadata()
    {
    }
}

public sealed class WorkbenchAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<WorkbenchAntiforgeryMetadata>() is not null)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await ApiProblemResults.InvalidRequest("Antiforgery validation failed.")
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
