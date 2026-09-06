// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Administration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class EntraPrincipalProvisioningTests
{
    [Theory]
    [InlineData("db_owner")]
    [InlineData("workbench_web]; ALTER ROLE db_owner ADD MEMBER attacker;--")]
    public void SetupCannotSelectAnUnapprovedRole(string role)
    {
        // GIVEN an identity manifest requesting excess authority, WHEN checked, THEN setup refuses it.
        var principals = Valid();
        principals[0] = principals[0] with { Role = role };
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(principals));
    }

    [Fact]
    public void EveryWorkloadRequiresADistinctIdentity()
    {
        // GIVEN web and migration entries sharing an identity, WHEN checked, THEN authority cannot combine.
        var principals = Valid();
        principals[1] = principals[1] with { ObjectId = principals[0].ObjectId };
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(principals));
    }

    [Fact]
    public void EveryRoleMustBePresentExactlyOnce()
    {
        // GIVEN a partial manifest, WHEN checked, THEN incomplete provisioning is rejected before SQL writes.
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(Valid()[..4]));
        var duplicate = Valid();
        duplicate[1] = duplicate[1] with { Role = duplicate[0].Role };
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(duplicate));
    }

    [Theory]
    [InlineData("bad'name")]
    [InlineData("bad]name")]
    [InlineData("")]
    public void NamesCannotContainSqlSyntax(string name)
    {
        // GIVEN an unsafe identifier, WHEN checked, THEN it cannot become SQL text.
        var principals = Valid();
        principals[0] = principals[0] with { Name = name };
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(principals));
    }

    [Fact]
    public void CompleteDistinctManifestIsAccepted()
    {
        // GIVEN a separate identity per approved role, WHEN checked, THEN setup can proceed.
        EntraPrincipalProvisioning.Validate(Valid());
    }

    private static EntraPrincipal[] Valid() => new[]
    {
        "workbench_web", "workbench_worker", "workbench_migrator", "workbench_operator", "workbench_storage_maintenance",
    }.Select((role, index) => new EntraPrincipal($"identity_{index}", Guid.NewGuid(), role)).ToArray();
}
