// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;
using Workbench.Server.Tenancy;
namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<DraftOrder> DraftOrders => Set<DraftOrder>();
    public DbSet<DraftOrderRequestReceipt> DraftOrderRequestReceipts => Set<DraftOrderRequestReceipt>();

    private void ConfigureDraftOrders(ModelBuilder builder)
    {
        var draft = builder.Entity<DraftOrder>();
        draft.ToTable("DraftOrders", "Purchasing", table =>
        {
            table.HasCheckConstraint("CK_DraftOrders_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_DraftOrders_Currency", "[Currency] IS NULL OR (DATALENGTH([Currency])=3 AND [Currency] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Z]%')");
            table.HasCheckConstraint("CK_DraftOrders_Notes", "[Notes] IS NULL OR DATALENGTH([Notes])<=20000");
            table.HasCheckConstraint("CK_DraftOrders_Content", "[ContentSchemaVersion]=1 AND ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=1048576");
            table.HasCheckConstraint("CK_DraftOrders_Timestamps", "[UpdatedAtUtc]>=[CreatedAtUtc] AND DATEPART(TZOFFSET,[CreatedAtUtc])=0 AND DATEPART(TZOFFSET,[UpdatedAtUtc])=0");
        });
        draft.HasKey(row => row.Id);
        draft.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        draft.Property(row => row.Title).HasMaxLength(200);
        draft.Property(row => row.SupplierName).HasMaxLength(200);
        draft.Property(row => row.Currency).HasMaxLength(3).IsUnicode(false);
        draft.Property(row => row.RowVersion).IsRowVersion();
        draft.HasIndex(row => new { row.TenantId, row.UpdatedAtUtc, row.Id }).IsDescending(false, true, true)
            .IncludeProperties(row => new { row.Title, row.SupplierName });
        draft.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        draft.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.CreatedByUserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        draft.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.UpdatedByUserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<DraftOrderRequestReceipt>();
        receipt.ToTable("DraftOrderRequestReceipts", "Purchasing", table =>
        {
            table.HasCheckConstraint("CK_DraftOrderRequestReceipts_RequestId", "[RequestId]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_DraftOrderRequestReceipts_Operation", "([Operation] COLLATE Latin1_General_100_BIN2='Create' AND [ExpectedRowVersion] IS NULL) OR ([Operation] COLLATE Latin1_General_100_BIN2='Update' AND [ExpectedRowVersion] IS NOT NULL)");
            table.HasCheckConstraint("CK_DraftOrderRequestReceipts_Fingerprint", "[FingerprintVersion]=1");
            table.HasCheckConstraint("CK_DraftOrderRequestReceipts_Completed", "DATEPART(TZOFFSET,[CompletedAtUtc])=0");
        });
        receipt.HasKey(row => new { row.TenantId, row.RequestId });
        receipt.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        receipt.Property(row => row.Operation).HasMaxLength(6).IsUnicode(false);
        receipt.Property(row => row.ExpectedRowVersion).HasColumnType("binary(8)");
        receipt.Property(row => row.ResultRowVersion).HasColumnType("binary(8)");
        receipt.Property(row => row.InputFingerprint).HasColumnType("binary(32)");
        receipt.HasOne<DraftOrder>().WithMany().HasForeignKey(row => new { row.TenantId, row.DraftOrderId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.ActorUserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
