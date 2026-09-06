// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Extensions.Configuration;
using Workbench.Server.Operations;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class OperationalConfigurationTests
{
    [Fact]
    public void MessageDeliveryCannotOverrideTheInstallationOrigin()
    {
        // GIVEN worker configuration with conflicting URL authorities.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicOrigin"] = "https://workbench.example",
            ["Smtp:PublicOrigin"] = "https://other.example",
        }).Build();
        // WHEN SMTP options are selected outside the web host, THEN the conflicting configuration is rejected.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.ReadSmtp(configuration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesystemAcceptsAStableInstallationInBothProfiles(bool development)
    {
        // GIVEN an existing durable root bound to a nonempty installation UUID.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:Root"] = Path.GetTempPath(),
            ["Storage:DurableVolume"] = "true",
            ["Storage:InstallationId"] = "79289486-b55b-43d4-9dd7-259ff3c4a634",
        }).Build();
        // WHEN the profile starts and constructs a provider,
        OperationalConfiguration.Validate(configuration, development);
        var store = Assert.IsType<Workbench.Server.Storage.FileSystemBlobStore>(OperationalConfiguration.CreateStore(configuration));
        // THEN it opens the configured root and retains its binding across reconstruction.
        await store.CheckReadyAsync(CancellationToken.None);
        Assert.Equal(store.Alias, OperationalConfiguration.CreateStore(configuration)!.Alias);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void FilesystemCannotStartOrCreateAProviderWithoutAnInstallationId(string? installation)
    {
        // GIVEN a durable filesystem root but no usable installation identity.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:Root"] = Path.GetTempPath(),
            ["Storage:DurableVolume"] = "true",
            ["Storage:InstallationId"] = installation,
        }).Build();
        // WHEN either profile validates or the provider factory is called directly,
        // THEN no provider can issue aliases that maintenance cannot retain.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.Validate(configuration, development: false));
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.Validate(configuration, development: true));
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.CreateStore(configuration));
    }

    [Fact]
    public void ChangingTheStorageLocationCannotReuseItsDurableAlias()
    {
        // GIVEN two filesystem configurations with different physical roots.
        var first = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:InstallationId"] = "79289486-b55b-43d4-9dd7-259ff3c4a634",
            ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "first"),
        }).Build();
        var second = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:InstallationId"] = "79289486-b55b-43d4-9dd7-259ff3c4a634",
            ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "second"),
        }).Build();
        // WHEN their providers are selected, THEN durable references cannot silently change location.
        Assert.NotEqual(OperationalConfiguration.CreateStore(first)!.Alias, OperationalConfiguration.CreateStore(second)!.Alias);
        Assert.Equal(OperationalConfiguration.CreateStore(first)!.Alias, OperationalConfiguration.CreateStore(first)!.Alias);
    }

    [Fact]
    public void ProductionCannotUseUnconfiguredStorage()
    {
        // GIVEN no selected durable provider, WHEN production configuration is checked,
        // THEN startup fails closed.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.Validate(new ConfigurationBuilder().Build(), development: false));
    }

    [Fact]
    public void MultipleReplicasCannotUseAnUnsharedFilesystem()
    {
        // GIVEN two replicas configured with a private local volume.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:Root"] = Path.GetTempPath(),
            ["Storage:DurableVolume"] = "true",
            ["Storage:InstallationId"] = "79289486-b55b-43d4-9dd7-259ff3c4a634",
            ["Deployment:Replicas"] = "2",
        }).Build();
        // WHEN validated, THEN unsafe replica topology is rejected.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.Validate(configuration, development: false));
    }
}
