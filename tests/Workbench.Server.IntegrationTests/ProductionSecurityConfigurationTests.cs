// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Workbench.Server.Security;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ProductionSecurityConfigurationTests
{
    [Fact]
    public async Task ProductionRejectsMissingDataProtectionCertificate()
    {
        var validator = new ProductionSecurityConfigurationValidator(
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("data-protection certificate", error.Message, StringComparison.Ordinal);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Workbench.Server.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
