// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Workbench.Server.Security;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ProductionSecurityConfigurationTests
{
    [Theory]
    [InlineData(null, "workbench.example")]
    [InlineData("http://workbench.example", "workbench.example")]
    [InlineData("https://workbench.example/path", "workbench.example")]
    [InlineData("https://user@workbench.example", "workbench.example")]
    [InlineData("https://workbench.example?x=1", "workbench.example")]
    [InlineData("https://workbench.example#token", "workbench.example")]
    [InlineData("https://workbench.example", "*")]
    [InlineData("https://workbench.example", "other.example")]
    public async Task ProductionRejectsAnUnsafePublicOrigin(string? origin, string hosts)
    {
        // GIVEN otherwise valid production configuration with an unsafe public origin or host policy.
        var settings = ValidSettings();
        settings["PublicOrigin"] = origin;
        settings["AllowedHosts"] = hosts;
        var validator = Validator(settings);
        // WHEN production starts, THEN external URL authority fails closed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProductionRejectsConflictingMessageOrigin()
    {
        // GIVEN a message origin that contradicts the installation origin.
        var settings = ValidSettings();
        settings["Smtp:PublicOrigin"] = "https://attacker.example";
        // WHEN production starts, THEN provider-specific links cannot override deployment authority.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProductionAcceptsExplicitCanonicalOrigin()
    {
        // GIVEN one canonical HTTPS origin and an explicit host and proxy.
        var validator = Validator(ValidSettings());
        // WHEN production starts, THEN the configuration is accepted.
        await validator.StartAsync(CancellationToken.None);
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["DataProtection:CertificatePath"] = "deployment-certificate.pfx",
        ["ConnectionStrings:Workbench"] = "Server=database;Database=workbench",
        ["TenantContext:ProofKey"] = Convert.ToBase64String(new byte[32]),
        ["ReverseProxy:KnownProxy"] = "10.42.0.2",
        ["PublicOrigin"] = "https://workbench.example",
        ["AllowedHosts"] = "workbench.example",
    };

    private static ProductionSecurityConfigurationValidator Validator(Dictionary<string, string?> settings) => new(
        new TestHostEnvironment { EnvironmentName = Environments.Production },
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), [], []);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Hosted")]
    public async Task ProductionRejectsMissingDataProtectionCertificate(string environment)
    {
        var validator = new ProductionSecurityConfigurationValidator(
            new TestHostEnvironment { EnvironmentName = environment },
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
