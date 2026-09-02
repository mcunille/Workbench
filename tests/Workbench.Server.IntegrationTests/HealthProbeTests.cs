// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Workbench.Server.Health;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class HealthProbeTests
{
    [Fact]
    public async Task HealthyReadinessReturnsSuccessfulExitCode()
    {
        using var client = new HttpMessageInvoker(new StaticResponseHandler(HttpStatusCode.OK));

        var exitCode = await HealthProbe.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/ready"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UnhealthyReadinessReturnsFailedExitCode()
    {
        using var client = new HttpMessageInvoker(
            new StaticResponseHandler(HttpStatusCode.ServiceUnavailable));

        var exitCode = await HealthProbe.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/ready"));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task UnreachableReadinessReturnsFailedExitCode()
    {
        using var client = new HttpMessageInvoker(new FailingHandler());

        var exitCode = await HealthProbe.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/ready"));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task TimedOutReadinessReturnsFailedExitCode()
    {
        using var client = new HttpMessageInvoker(new CanceledHandler());

        var exitCode = await HealthProbe.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/ready"));

        Assert.Equal(1, exitCode);
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("unreachable"));
        }
    }

    private sealed class CanceledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out"));
        }
    }
}
