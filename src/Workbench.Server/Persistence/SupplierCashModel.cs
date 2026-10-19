// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierCashSource> SupplierCashSources => Set<SupplierCashSource>();
    public DbSet<SupplierCashSettlement> SupplierCashSettlements => Set<SupplierCashSettlement>();
    public DbSet<SupplierCashVersion> SupplierCashVersions => Set<SupplierCashVersion>();

    private void ConfigureSupplierCash(ModelBuilder builder)
    {
        var source = builder.Entity<SupplierCashSource>();
        source.ToTable("SupplierCashSources", "Purchasing", t => t.HasCheckConstraint("CK_SupplierCashSources_Source",
            "[Amount]>0 AND [ActualDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND [Kind] COLLATE Latin1_General_100_BIN2 IN ('SupplierRefund','SupplierRefundRepayment') AND [Method] COLLATE Latin1_General_100_BIN2 IN ('Bank','Cash') AND [Method]=[FundingAccountPurpose] AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144"));
        source.HasKey(x => new { x.TenantId, x.Id });
        source.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        source.HasIndex(x => new { x.TenantId, x.RevisionId }).IsUnique();
        source.HasIndex(x => new { x.TenantId, x.SupplierId, x.PurchaseOrderId, x.PostingDate });
        source.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false);
        source.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        source.Property(x => x.Amount).HasPrecision(28, 4);
        source.Property(x => x.Method).HasMaxLength(12).IsUnicode(false);
        source.Property(x => x.FundingAccountPurpose).HasMaxLength(40);
        source.Property(x => x.Reference).HasMaxLength(200);
        source.Property(x => x.Notes).HasMaxLength(2000);
        source.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.FundingAccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId })
            .OnDelete(DeleteBehavior.Restrict);

        var settlement = builder.Entity<SupplierCashSettlement>();
        settlement.ToTable("SupplierCashSettlements", "Purchasing", t => t.HasCheckConstraint("CK_SupplierCashSettlements_Amount", "[Amount]>0"));
        settlement.HasKey(x => new { x.TenantId, x.Id });
        settlement.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        settlement.Property(x => x.Amount).HasPrecision(28, 4);
        settlement.HasIndex(x => new { x.TenantId, x.CashSourceId, x.ItemId }).IsUnique();
        settlement.HasOne<SupplierCashSource>().WithMany().HasForeignKey(x => new { x.TenantId, x.CashSourceId }).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.ItemId }).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<SupplierItemMovement>().WithMany().HasForeignKey(x => new { x.TenantId, x.MovementId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<JournalLineRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId, x.JournalOrdinal })
            .HasPrincipalKey(x => new { x.TenantId, x.JournalId, x.Ordinal }).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var version = builder.Entity<SupplierCashVersion>();
        version.ToTable("SupplierCashVersions", "Purchasing");
        version.HasKey(x => new { x.TenantId, x.CashSourceId });
        version.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        version.Property(x => x.RowVersion).IsRowVersion();
        version.HasOne<SupplierCashSource>().WithMany().HasForeignKey(x => new { x.TenantId, x.CashSourceId }).OnDelete(DeleteBehavior.Restrict);
    }
}
