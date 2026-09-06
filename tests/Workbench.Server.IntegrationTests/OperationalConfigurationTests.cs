// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Extensions.Configuration;
using Workbench.Server.Operations;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class OperationalConfigurationTests
{
    [Fact]
    public void ChangingTheStorageLocationCannotReuseItsDurableAlias()
    {
        // GIVEN two filesystem configurations with different physical roots.
        var first = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "first"),
        }).Build();
        var second = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
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
            ["Deployment:Replicas"] = "2",
        }).Build();
        // WHEN validated, THEN unsafe replica topology is rejected.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.Validate(configuration, development: false));
    }
}
