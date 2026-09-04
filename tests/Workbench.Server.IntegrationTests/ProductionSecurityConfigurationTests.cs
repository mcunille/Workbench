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
            new ConfigurationBuilder().Build(),
            [],
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("data-protection certificate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionRejectsPublicRecoveryWithoutProductionProviders()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = "deployment-certificate.pfx",
                ["ConnectionStrings:Workbench"] = "Server=database;Database=workbench",
                ["TenantContext:ProofKey"] = Convert.ToBase64String(new byte[32]),
                ["ReverseProxy:KnownProxy"] = "127.0.0.1",
                ["Identity:PublicRecoveryEnabled"] = "true",
            })
            .Build();
        var validator = new ProductionSecurityConfigurationValidator(
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            configuration,
            [],
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("message delivery", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionRejectsMissingTenantContextProofKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = "deployment-certificate.pfx",
                ["ConnectionStrings:Workbench"] = "Server=database;Database=workbench",
                ["ReverseProxy:KnownProxy"] = "127.0.0.1",
            })
            .Build();
        var validator = new ProductionSecurityConfigurationValidator(
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            configuration,
            [],
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("tenant context proof key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-ip-address")]
    public async Task ProductionRejectsMissingOrInvalidTrustedProxy(string? proxy)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = "deployment-certificate.pfx",
                ["ConnectionStrings:Workbench"] = "Server=database;Database=workbench",
                ["TenantContext:ProofKey"] = Convert.ToBase64String(new byte[32]),
                ["ReverseProxy:KnownProxy"] = proxy,
            })
            .Build();
        var validator = new ProductionSecurityConfigurationValidator(
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            configuration,
            [],
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("trusted proxy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Workbench.Server.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
