// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Inventory;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(PhotoProcessorCollection.Name)]
public sealed class DocumentUploadLimitsTests
{
    [Fact]
    public async Task BusyUploadRejectsBeforeAnotherBodyCanBeBufferedAndReleasesCapacity()
    {
        // GIVEN one admitted upload held before it reads the request body.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var middleware = new DocumentUploadLimitsMiddleware(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
        });
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new DocumentUploadMetadata()), "upload"));
            context.Request.Headers["X-CSRF-TOKEN"] = "test";
            context.Response.Body = new MemoryStream();
            return context;
        }
        var first = middleware.InvokeAsync(Context());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // WHEN another upload arrives THEN reject promptly before downstream parsing.
            var second = Context();
            await middleware.InvokeAsync(second).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(503, second.Response.StatusCode);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); await first; }
        // AND completion frees the process slot for a later retry.
        await middleware.InvokeAsync(Context());
        Assert.Equal(2, calls);
    }
}
