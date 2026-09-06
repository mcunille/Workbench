// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Extensions.Logging;
using Workbench.Server.Operations;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class TelemetryRedactionTests
{
    [Fact]
    public void ExportDropsSecretsFromMessagesExceptionsCategoriesAndScopes()
    {
        // GIVEN a central export pipeline receiving unsafe dependency diagnostics.
        using var output = new StringWriter();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new SafeTelemetryLoggerProvider(output)));
        var logger = factory.CreateLogger("credential-category-sentinel");
        using var scope = logger.BeginScope("session-scope-sentinel");
        // WHEN a dependency emits a credential and an unsafe exception.
        logger.LogError(new EventId(123), new IOException("reset-token-sentinel"),
            "Connection {ConnectionString}; content {Content}", "password-sentinel", "document-sentinel");
        // THEN only bounded event metadata reaches the actual export writer.
        var exported = output.ToString();
        Assert.Contains("123", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", exported, StringComparison.Ordinal);
    }
}
