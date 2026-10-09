// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Extensions.Configuration;
using Workbench.Server.Operations;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class RecoveryBindingTests
{
    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task EquivalentPhysicalAzureSourceIsRejected(string format)
    {
        await Task.Yield();
        // GIVEN an original binding and another spelling of the same physical installation.
        var id = Guid.NewGuid();
        var configuration = Config(id.ToString(), id.ToString(format).ToUpperInvariant(), "https://source.blob.core.windows.net/files");
        // WHEN validating isolation before provider operations.
        Assert.Throws<InvalidOperationException>(() => RecoveryBinding.Validate(configuration, Inventory(configuration)));
        // THEN different alias text cannot authorize writes to production.
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task OriginalAliasSpellingIsPreservedForCatalogMatching(string format)
    {
        await Task.Yield();
        // GIVEN a legitimate original alias using a noncanonical UUID spelling.
        var configuration = Config(Guid.NewGuid().ToString(format).ToUpperInvariant(), Guid.NewGuid().ToString(), "https://recovered.blob.core.windows.net/files");
        // WHEN validating a physically independent destination.
        var alias = RecoveryBinding.Validate(configuration, Inventory(configuration));
        // THEN catalog matching uses the exact SQL-verified original alias.
        Assert.Equal(OperationalConfiguration.ProviderAlias(configuration.GetSection("Recovery:Source")), alias);
    }

    [Fact]
    public async Task FilesystemRootCannotBeReusedWithAnotherInstallationId()
    {
        await Task.Yield();
        // GIVEN the same filesystem root with a changed UUID and platform-equivalent path spelling.
        var path = Path.GetFullPath(Path.GetTempPath());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["Storage:Root"] = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path,
            ["Storage:InstallationId"] = Guid.NewGuid().ToString(),
            ["Recovery:Source:Storage:Provider"] = "FileSystem",
            ["Recovery:Source:Storage:Root"] = path,
            ["Recovery:Source:Storage:InstallationId"] = Guid.NewGuid().ToString()
        }).Build();
        // WHEN validating before IO, THEN physical reuse is rejected despite the different alias.
        Assert.Throws<InvalidOperationException>(() => RecoveryBinding.Validate(config, Inventory(config)));
    }

    [Fact]
    public async Task InventedOriginalSourceCannotAuthorizeCleanup()
    {
        await Task.Yield();
        // GIVEN a source declaration inconsistent with retained SQL bindings.
        var config = Config(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "https://recovered.blob.core.windows.net/files");
        var inventory = Inventory(config);
        inventory = inventory with { Rows = [inventory.Rows[0] with { ProviderAlias = "different" }] };
        // WHEN validating isolation, THEN a physically different but unverified source is rejected.
        Assert.Throws<InvalidOperationException>(() => RecoveryBinding.Validate(config, inventory));
    }
    [Fact]
    public async Task PurgedHistoryFromEarlierProviderDoesNotBlockAnotherRecovery()
    {
        await Task.Yield();
        // GIVEN retained content in the current store and purged history from a previous relocation.
        var config = Config(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "https://recovered.blob.core.windows.net/files");
        var inventory = Inventory(config);
        inventory = inventory with { Rows = [inventory.Rows[0], inventory.Rows[0] with { RevisionId = Guid.NewGuid(), ProviderAlias = "previous", State = 3 }] };
        // WHEN a new isolated recovery is planned, THEN only content-bearing bindings require the current source.
        Assert.Equal(inventory.Rows[0].ProviderAlias, RecoveryBinding.Validate(config, inventory));
    }

    [Theory]
    [InlineData("https://files.example.com/workbench")]
    [InlineData("https://source.privatelink.blob.core.windows.net/workbench")]
    public async Task AlternateAzureEndpointCannotConcealPhysicalReuse(string target)
    {
        await Task.Yield();
        // GIVEN an endpoint alias that could route to the original account.
        var config = Config(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), target);
        // WHEN validating isolation, THEN only canonical account/container endpoints are accepted.
        Assert.Throws<InvalidOperationException>(() => RecoveryBinding.Validate(config, Inventory(config)));
    }

    private static IConfiguration Config(string sourceId, string targetId, string target) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:Provider"] = "Azure",
        ["Storage:ContainerUri"] = target,
        ["Storage:InstallationId"] = targetId,
        ["Recovery:Source:Storage:Provider"] = "Azure",
        ["Recovery:Source:Storage:ContainerUri"] = "https://source.blob.core.windows.net/files",
        ["Recovery:Source:Storage:InstallationId"] = sourceId
    }).Build();
    private static RecoveryInventory Inventory(IConfiguration configuration) => new("Workbench", "restore", 1,
        [new(Guid.NewGuid(), Guid.NewGuid(), OperationalConfiguration.ProviderAlias(configuration.GetSection("Recovery:Source")), 1, "00", 1, "version")]);
}
