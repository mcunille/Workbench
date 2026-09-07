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
        principals[1] = principals[1] with { PrincipalId = principals[0].PrincipalId };
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

    [Fact]
    public void ExplicitPrincipalAndClientIdsAreAccepted()
    {
        // GIVEN Azure reports both principal and application IDs for five separate workloads.
        var identities = Valid().Select(principal => new
        {
            principal.Name,
            principal.Role,
            PrincipalId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
        });
        var json = System.Text.Json.JsonSerializer.Serialize(identities);
        var principals = System.Text.Json.JsonSerializer.Deserialize<EntraPrincipal[]>(json)!;

        // WHEN validating the explicit identity contract, THEN provisioning can proceed.
        EntraPrincipalProvisioning.Validate(principals);
    }

    [Fact]
    public void VersionedManifestPreservesBothIdentityIds()
    {
        // GIVEN the operator obtained distinct IDs from Azure for each role.
        var expected = Valid();
        var json = System.Text.Json.JsonSerializer.Serialize(new { version = 1, identities = expected });

        // WHEN parsing the versioned contract, THEN neither ID is substituted or lost.
        Assert.Equal(expected, EntraPrincipalProvisioning.ParseManifest(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"identities\":[]}")]
    [InlineData("{\"version\":1,\"identities\":null}")]
    [InlineData("[{\"objectId\":\"secret-diagnostic-marker\"}]")]
    [InlineData("{\"version\":1,\"identities\":[{\"objectId\":\"secret-diagnostic-marker\"}]}")]
    [InlineData("not-json-secret-diagnostic-marker")]
    public void InvalidAndLegacyManifestErrorsAreActionableAndDoNotEchoInput(string json)
    {
        // GIVEN legacy, malformed, or unsupported input, WHEN parsing before any SQL connection.
        var error = Assert.Throws<InvalidEntraManifestException>(() => EntraPrincipalProvisioning.ParseManifest(json));

        // THEN only the safe new-contract instructions are reported.
        Assert.Contains("version 1", error.Message);
        Assert.Contains("principalId and clientId", error.Message);
        Assert.DoesNotContain("secret-diagnostic-marker", error.Message);
    }

    [Fact]
    public void SqlSidUsesClientIdInSqlGuidByteOrder()
    {
        // GIVEN distinct principal and client IDs, including a known non-symmetric GUID.
        var principal = Valid()[0] with
        {
            ClientId = Guid.Parse("594eb3f9-8249-4582-b76d-00bf4e76d5c0"),
        };

        // WHEN constructing an Azure SQL application SID, THEN it encodes the client ID.
        Assert.Equal("F9B34E5949828245B76D00BF4E76D5C0", Convert.ToHexString(principal.GetSqlSid()));
        Assert.NotEqual(principal.PrincipalId.ToByteArray(), principal.GetSqlSid());
    }

    [Theory]
    [InlineData("empty-principal")]
    [InlineData("empty-client")]
    [InlineData("duplicate-client")]
    [InlineData("same-ids")]
    [InlineData("cross-role-ids")]
    [InlineData("duplicate-name")]
    public void IdentityAmbiguitiesAreRejected(string scenario)
    {
        // GIVEN missing or overlapping identity identifiers in the proposed mapping.
        var principals = Valid();
        principals[1] = scenario switch
        {
            "empty-principal" => principals[1] with { PrincipalId = Guid.Empty },
            "empty-client" => principals[1] with { ClientId = Guid.Empty },
            "duplicate-client" => principals[1] with { ClientId = principals[0].ClientId },
            "same-ids" => principals[1] with { ClientId = principals[1].PrincipalId },
            "cross-role-ids" => principals[1] with { ClientId = principals[0].PrincipalId },
            _ => principals[1] with { Name = principals[0].Name.ToUpperInvariant() },
        };

        // WHEN validating before SQL, THEN setup refuses ambiguous authority.
        Assert.Throws<ArgumentException>(() => EntraPrincipalProvisioning.Validate(principals));
    }

    private static EntraPrincipal[] Valid() => new[]
    {
        "workbench_web", "workbench_worker", "workbench_migrator", "workbench_operator", "workbench_storage_maintenance",
    }.Select((role, index) => new EntraPrincipal($"identity_{index}", Guid.NewGuid(), Guid.NewGuid(), role)).ToArray();
}
