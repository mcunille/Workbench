// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<JournalPolicyFreeze> JournalPolicyFreezes => Set<JournalPolicyFreeze>();
    public DbSet<JournalSourceEvent> JournalSourceEvents => Set<JournalSourceEvent>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLineRow> JournalLines => Set<JournalLineRow>();
    public DbSet<JournalPostingReceipt> JournalPostingReceipts => Set<JournalPostingReceipt>();

    private void ConfigureJournal(ModelBuilder builder)
    {
        var freeze = builder.Entity<JournalPolicyFreeze>();
        freeze.ToTable("PolicyFreezes", "Accounting");
        freeze.HasKey(x => x.TenantId);
        freeze.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        freeze.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        freeze.Property(x => x.StartApproach).HasMaxLength(40);
        freeze.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        freeze.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.FirstJournalId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var source = builder.Entity<JournalSourceEvent>();
        source.ToTable("SourceEvents", "Accounting", t => t.HasCheckConstraint("CK_SourceEvents_Snapshot", "ISJSON([SnapshotJson], OBJECT) = 1 AND DATALENGTH([SnapshotJson]) <= 262144 AND DATALENGTH([SnapshotSha256]) = 32"));
        source.HasKey(x => x.Id);
        source.HasAlternateKey(x => new { x.TenantId, x.Id });
        source.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        source.Property(x => x.SourceKind).HasMaxLength(64);
        source.Property(x => x.EventKind).HasMaxLength(64);
        source.Property(x => x.Reference).HasMaxLength(200);
        source.Property(x => x.Reason).HasMaxLength(2000);
        source.Property(x => x.SnapshotSha256).HasMaxLength(32).IsFixedLength();
        source.HasIndex(x => new { x.TenantId, x.SourceKind, x.SourceId, x.SourceRevision, x.EventKind }).IsUnique();
        source.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var entry = builder.Entity<JournalEntry>();
        entry.ToTable("JournalEntries", "Accounting", t => t.HasCheckConstraint("CK_JournalEntries_Balance", "[DebitTotal] = [CreditTotal] AND [DebitTotal] > 0 AND [Scale] BETWEEN 0 AND 4"));
        entry.HasKey(x => x.Id);
        entry.HasAlternateKey(x => new { x.TenantId, x.Id });
        entry.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        entry.Property(x => x.Sequence).ValueGeneratedOnAdd();
        entry.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        entry.Property(x => x.Reference).HasMaxLength(200);
        entry.Property(x => x.Reason).HasMaxLength(2000);
        entry.Property(x => x.DebitTotal).HasPrecision(28, 4);
        entry.Property(x => x.CreditTotal).HasPrecision(28, 4);
        entry.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
        entry.HasIndex(x => new { x.TenantId, x.SourceEventId }).IsUnique();
        entry.HasIndex(x => new { x.TenantId, x.PostingDate, x.Sequence });
        entry.HasOne<JournalSourceEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.SourceEventId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var line = builder.Entity<JournalLineRow>();
        line.ToTable("JournalLines", "Accounting", t => t.HasCheckConstraint("CK_JournalLines_OneSide", "[Ordinal] BETWEEN 1 AND 1000 AND (([Debit] > 0 AND [Credit] = 0) OR ([Credit] > 0 AND [Debit] = 0))"));
        line.HasKey(x => new { x.TenantId, x.JournalId, x.Ordinal });
        line.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        line.Property(x => x.AccountCode).HasMaxLength(32).UseCollation("Latin1_General_100_BIN2");
        line.Property(x => x.AccountName).HasMaxLength(160);
        line.Property(x => x.AccountType).HasMaxLength(20);
        line.Property(x => x.AccountPurpose).HasMaxLength(40);
        line.Property(x => x.Debit).HasPrecision(28, 4);
        line.Property(x => x.Credit).HasPrecision(28, 4);
        line.HasIndex(x => new { x.TenantId, x.AccountId, x.JournalId, x.Ordinal });
        line.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        line.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<JournalPostingReceipt>();
        receipt.ToTable("PostingReceipts", "Accounting", t => t.HasCheckConstraint("CK_PostingReceipts_Input", "ISJSON([CanonicalInput], OBJECT) = 1 AND DATALENGTH([CanonicalInput]) <= 262144 AND DATALENGTH([InputSha256]) = 32"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.SourceCommandKind).HasMaxLength(64);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<JournalSourceEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.SourceEventId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
