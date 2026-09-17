// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BlobManifestValidationTests
{
    [Theory]
    [InlineData("20260907194500_AddItemDetailEditing")]
    [InlineData("20260907224158_AddItemArchiving")]
    [InlineData("20260907225320_AddOnlineRecovery")]
    [InlineData("20260908010000_AddItemRestoration")]
    [InlineData("20260909034719_AddAcquisitionContext")]
    [InlineData("20260910071000_AddSharedAcquisitions")]
    [InlineData("20260911184933_AddAcquisitionDocuments")]
    [InlineData("20260912030844_AddDraftSupplierOrders")]
    [InlineData("20260912064156_AddSupplierIdentityAndPurchaseReferences")]
    [InlineData("20260916183834_AddStructuredDraftOrderLines")]
    [InlineData("20260917010000_AddSupplierBasedDraftPricing")]
    [InlineData("20260917080000_ConsolidateBetaDraftCommands")]
    [InlineData("20260918010000_RemoveHistoricalDraftReplay")]
    [InlineData("20260912033355_TightenDraftSourceLinkValidation")]
    [InlineData("20260912045432_AddDraftOrderDeletion")]
    public void EverySupportedSchemaAcceptsAnExactManifest(string schema)
    {
        // GIVEN an exact retained-content manifest from each supported release.
        var manifest = Manifest() with { SchemaVersion = schema };
        // WHEN validating its database, installation and ordered entries THEN compatibility is retained.
        StorageMaintenanceCommand.ValidateManifest(manifest, manifest.Database, manifest.InstallationId, manifest.Entries);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("schema")]
    [InlineData("schema-case")]
    [InlineData("database")]
    [InlineData("database-case")]
    [InlineData("installation")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("order")]
    [InlineData("tenant")]
    [InlineData("revision")]
    [InlineData("provider")]
    [InlineData("length")]
    [InlineData("digest")]
    public void EveryManifestBindingAndRetainedEntryMustMatch(string kind)
    {
        // GIVEN the unchanged rejection cases characterized through the maintenance command before extraction.
        var manifest = Manifest();
        // WHEN one binding or retained-entry invariant differs THEN exact-pair recovery cannot proceed.
        var error = Assert.Throws<InvalidDataException>(() => StorageMaintenanceCommand.ValidateManifest(
            Mismatch(manifest, kind), manifest.Database, manifest.InstallationId, manifest.Entries));
        Assert.Equal("The manifest does not match the restored database.", error.Message);
    }

    private static BlobManifest Manifest() => new(1, "20260912064156_AddSupplierIdentityAndPurchaseReferences",
        Guid.NewGuid(), Guid.NewGuid(), "restored-database", DateTimeOffset.UtcNow,
        [new(Guid.NewGuid(), Guid.NewGuid(), "provider", 1, "AA"), new(Guid.NewGuid(), Guid.NewGuid(), "provider", 2, "BB")]);

    private static BlobManifest Mismatch(BlobManifest manifest, string kind) => kind switch
    {
        "format" => manifest with { Version = 2 },
        "schema" => manifest with { SchemaVersion = "unsupported" },
        "schema-case" => manifest with { SchemaVersion = manifest.SchemaVersion.ToUpperInvariant() },
        "database" => manifest with { Database = "another-database" },
        "database-case" => manifest with { Database = manifest.Database.ToUpperInvariant() },
        "installation" => manifest with { InstallationId = Guid.NewGuid() },
        "missing" => manifest with { Entries = manifest.Entries.Skip(1).ToArray() },
        "extra" => manifest with { Entries = [.. manifest.Entries, manifest.Entries[0]] },
        "order" => manifest with { Entries = manifest.Entries.Reverse().ToArray() },
        "tenant" => ChangedEntry(manifest, manifest.Entries[0] with { TenantId = Guid.NewGuid() }),
        "revision" => ChangedEntry(manifest, manifest.Entries[0] with { RevisionId = Guid.NewGuid() }),
        "provider" => ChangedEntry(manifest, manifest.Entries[0] with { ProviderAlias = "another-provider" }),
        "length" => ChangedEntry(manifest, manifest.Entries[0] with { Length = manifest.Entries[0].Length + 1 }),
        "digest" => ChangedEntry(manifest, manifest.Entries[0] with { Sha256 = "different-digest" }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static BlobManifest ChangedEntry(BlobManifest manifest, BlobManifestEntry entry) =>
        manifest with { Entries = [entry, .. manifest.Entries.Skip(1)] };
}
