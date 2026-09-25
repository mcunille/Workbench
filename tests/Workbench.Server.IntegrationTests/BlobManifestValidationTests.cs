// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Storage;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BlobManifestValidationTests
{
    public static IEnumerable<object[]> SupportedReleaseSchemas => CurrentSchema.Migrations
        .SkipWhile(schema => schema != "20260907194500_AddItemDetailEditing")
        .Select(schema => new object[] { schema });

    [Theory]
    [MemberData(nameof(SupportedReleaseSchemas))]
    public void KnownReleasesFromBackupSupportBoundaryAcceptAnExactManifest(string schema)
    {
        // GIVEN a known release at or after the first supported backup boundary.
        var manifest = Manifest() with { SchemaVersion = schema };
        // WHEN validating THEN new releases and their predecessors need no separate compatibility registration.
        StorageMaintenanceCommand.ValidateManifest(manifest, manifest.Database, manifest.InstallationId, manifest.Entries);
    }

    [Theory]
    [InlineData("20260907194500_AddItemDetailEditing")]
    [InlineData("20260916183834_AddStructuredDraftOrderLines")]
    [InlineData("20260918030000_AddPurchaseOrderCommitment")]
    [InlineData("20260918040000_HardenPurchaseOrderCommitmentValidation")]
    [InlineData("20260918050000_ProjectRetainedPurchaseOrderLines")]
    [InlineData("20260912033355_TightenDraftSourceLinkValidation")]
    [InlineData("20260912045432_AddDraftOrderDeletion")]
    public void FirstSupportedBoundaryAndRetiredMarkersRemainAccepted(string schema)
    {
        // GIVEN an independently pinned boundary or a retired marker absent from the current migration inventory.
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

    [Fact]
    public void CurrentReleaseAcceptsAnExactManifest()
    {
        // GIVEN a manifest emitted by this release, independently of historical compatibility fixtures.
        var manifest = Manifest() with { SchemaVersion = CurrentSchema.MigrationId };
        // WHEN validating the paired backup THEN the current schema is supported.
        StorageMaintenanceCommand.ValidateManifest(manifest, manifest.Database, manifest.InstallationId, manifest.Entries);
    }

    [Theory]
    [InlineData("20260904061204_InitialSchema")]
    [InlineData("20260907082353_AddItemPhotographs")]
    [InlineData("20260921051844_UnknownSchema")]
    [InlineData("99999999999999_FutureSchema")]
    public void KnownButUnsupportedAndFutureSchemasRemainRejected(string schema)
    {
        // GIVEN a known pre-support release or an unknown marker within or beyond the supported date range.
        var manifest = Manifest() with { SchemaVersion = schema };
        // WHEN validating THEN neither inventory membership nor ordering grants compatibility.
        Assert.Throws<InvalidDataException>(() => StorageMaintenanceCommand.ValidateManifest(
            manifest, manifest.Database, manifest.InstallationId, manifest.Entries));
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
