// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Gemology;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<GemReferenceEntry> GemReferenceEntries => Set<GemReferenceEntry>();
    public DbSet<GemReferenceAlias> GemReferenceAliases => Set<GemReferenceAlias>();
    public DbSet<GemReferenceSourceAssertion> GemReferenceSourceAssertions => Set<GemReferenceSourceAssertion>();
    public DbSet<GemReferenceLocalityAssertion> GemReferenceLocalityAssertions => Set<GemReferenceLocalityAssertion>();

    private static void ConfigureGemReference(ModelBuilder builder)
    {
        var entry = builder.Entity<GemReferenceEntry>();
        entry.ToTable("Entries", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemEntries_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_GemEntries_Kind", "[MaterialKind] COLLATE Latin1_General_100_BIN2 IN ('mineral','mineraloid','organic','rockAggregate')");
            table.HasCheckConstraint("CK_GemEntries_Name", "LEN(LTRIM(RTRIM([CommonName])))>0");
            table.HasCheckConstraint("CK_GemEntries_Species", "[MaterialKind] COLLATE Latin1_General_100_BIN2<>'mineral' OR ([Species] IS NOT NULL AND LEN(LTRIM(RTRIM([Species])))>0)");
            table.HasCheckConstraint("CK_GemEntries_OptionalText", "([Group] IS NULL OR LEN(LTRIM(RTRIM([Group])))>0) AND ([Species] IS NULL OR LEN(LTRIM(RTRIM([Species])))>0) AND ([Variety] IS NULL OR LEN(LTRIM(RTRIM([Variety])))>0) AND ([Description] IS NULL OR LEN(LTRIM(RTRIM([Description])))>0) AND ([RetirementExplanation] IS NULL OR LEN(LTRIM(RTRIM([RetirementExplanation])))>0)");
            table.HasCheckConstraint("CK_GemEntries_Retirement", "([IsRetired]=0 AND [RedirectEntryId] IS NULL AND [RetirementExplanation] IS NULL) OR ([IsRetired]=1 AND ([RetirementExplanation] IS NOT NULL OR [RedirectEntryId] IS NOT NULL))");
            table.HasCheckConstraint("CK_GemEntries_Redirect", "[RedirectEntryId] IS NULL OR [RedirectEntryId]<>[Id]");
        });
        entry.HasKey(row => row.Id);
        entry.Property(row => row.MaterialKind).HasMaxLength(20);
        entry.Property(row => row.CommonName).HasMaxLength(200);
        entry.Property(row => row.Group).HasMaxLength(200);
        entry.Property(row => row.Species).HasMaxLength(200);
        entry.Property(row => row.Variety).HasMaxLength(200);
        entry.Property(row => row.Description).HasMaxLength(2000);
        entry.Property(row => row.RetirementExplanation).HasMaxLength(2000);
        entry.Property(row => row.IdentityKey).HasColumnType("binary(32)");
        entry.Property(row => row.RowVersion).IsRowVersion();
        entry.HasIndex(row => row.IdentityKey).IsUnique().HasFilter("[IsRetired] = 0");
        entry.HasIndex(row => new { row.CommonName, row.Id });
        entry.HasOne<GemReferenceEntry>().WithMany().HasForeignKey(row => row.RedirectEntryId).OnDelete(DeleteBehavior.Restrict);

        var alias = builder.Entity<GemReferenceAlias>();
        alias.ToTable("Aliases", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemAliases_Name", "LEN(LTRIM(RTRIM([Name])))>0 AND LEN([NormalizedName])>0");
            table.HasCheckConstraint("CK_GemAliases_Position", "[Position]>=0 AND [Position]<20");
        });
        alias.HasKey(row => new { row.EntryId, row.Position });
        alias.Property(row => row.Name).HasMaxLength(200);
        alias.Property(row => row.NormalizedName).HasMaxLength(4000);
        alias.Property<byte[]>("NormalizedKey").IsRequired().HasColumnType("binary(32)")
            .HasComputedColumnSql("CONVERT(binary(32), HASHBYTES('SHA2_256', [NormalizedName]))", stored: true);
        alias.HasIndex("EntryId", "NormalizedKey").IsUnique().HasFilter(null);
        alias.HasOne<GemReferenceEntry>().WithMany(row => row.Aliases).HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Restrict);

        var source = builder.Entity<GemReferenceSourceAssertion>();
        source.ToTable("SourceAssertions", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemSources_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_GemSources_Field", "[Field] COLLATE Latin1_General_100_BIN2 IN ('materialKind','commonName','aliases','group','species','variety','description','notableLocality')");
            table.HasCheckConstraint("CK_GemSources_Identification", "LEN(LTRIM(RTRIM([Title])))>0 AND LEN(LTRIM(RTRIM([Publisher])))>0 AND (([Url] IS NOT NULL AND LEN(LTRIM(RTRIM([Url])))>0) OR ([Citation] IS NOT NULL AND LEN(LTRIM(RTRIM([Citation])))>0))");
            table.HasCheckConstraint("CK_GemSources_Dates", "[ReviewedOn]>'0001-01-01' AND ([AccessedOn] IS NULL OR [AccessedOn]>'0001-01-01')");
        });
        source.HasKey(row => row.Id);
        source.Property(row => row.Field).HasMaxLength(30);
        source.Property(row => row.Title).HasMaxLength(200);
        source.Property(row => row.Publisher).HasMaxLength(200);
        source.Property(row => row.Url).HasMaxLength(2000);
        source.Property(row => row.Citation).HasMaxLength(2000);
        source.HasAlternateKey(row => new { row.EntryId, row.Id, row.Field, row.ReviewedOn });
        source.HasOne<GemReferenceEntry>().WithMany(row => row.SourceAssertions).HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Restrict);

        var locality = builder.Entity<GemReferenceLocalityAssertion>();
        locality.ToTable("LocalityAssertions", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemLocality_Claim", "LEN(LTRIM(RTRIM([Place])))>0 AND LEN(LTRIM(RTRIM([Scope])))>0 AND [SourceField] COLLATE Latin1_General_100_BIN2='notableLocality'");
        });
        locality.HasKey(row => row.EntryId);
        locality.Property(row => row.Place).HasMaxLength(200);
        locality.Property(row => row.Scope).HasMaxLength(200);
        locality.Property(row => row.SourceField).HasMaxLength(30);
        locality.HasOne<GemReferenceEntry>().WithOne(row => row.NotableLocality).HasForeignKey<GemReferenceLocalityAssertion>(row => row.EntryId).OnDelete(DeleteBehavior.Restrict);
        locality.HasOne<GemReferenceSourceAssertion>().WithMany()
            .HasForeignKey(row => new { row.EntryId, row.SourceAssertionId, row.SourceField, row.ReviewedOn })
            .HasPrincipalKey(row => new { row.EntryId, row.Id, row.Field, row.ReviewedOn }).OnDelete(DeleteBehavior.Restrict);
    }
}
