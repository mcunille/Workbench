// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<AcquisitionDocument> AcquisitionDocuments => Set<AcquisitionDocument>();
    public DbSet<AcquisitionDocumentOperation> AcquisitionDocumentOperations => Set<AcquisitionDocumentOperation>();
    private void ConfigureAcquisitionDocuments(ModelBuilder builder)
    {
        var document = builder.Entity<AcquisitionDocument>();
        document.ToTable("AcquisitionDocuments", "Inventory", table =>
        {
            table.HasCheckConstraint("CK_AcquisitionDocuments_Label", "DATALENGTH([Label]) BETWEEN 2 AND 400");
            table.HasCheckConstraint("CK_AcquisitionDocuments_Content", "[Length] BETWEEN 1 AND 10485760 AND LEN([Sha256])=64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%'");
        });
        document.HasKey(row => row.Id);
        document.HasAlternateKey(row => new { row.TenantId, row.Id });
        document.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        document.Property(row => row.Label).HasMaxLength(200);
        document.Property(row => row.MediaType).HasMaxLength(100);
        document.Property(row => row.Extension).HasMaxLength(8);
        document.Property(row => row.Sha256).HasMaxLength(64);
        document.Property(row => row.RowVersion).IsRowVersion();
        document.HasIndex(row => new { row.TenantId, row.AcquisitionId, row.RemovedAtUtc });
        document.HasOne<Acquisition>().WithMany().HasForeignKey(row => new { row.TenantId, row.AcquisitionId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        document.HasOne<AttachmentRevision>().WithMany().HasForeignKey(row => new { row.TenantId, row.AttachmentId, row.RevisionId }).HasPrincipalKey(row => new { row.TenantId, row.AttachmentId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        var operation = builder.Entity<AcquisitionDocumentOperation>();
        operation.ToTable("AcquisitionDocumentOperations", "Inventory", table => table.HasCheckConstraint("CK_AcquisitionDocumentOperations_State", "[Kind] BETWEEN 0 AND 2 AND [State] BETWEEN 0 AND 2"));
        operation.HasKey(row => row.Id);
        operation.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        operation.HasIndex(row => new { row.TenantId, row.RequestId }).IsUnique();
        operation.HasIndex(row => new { row.TenantId, row.AcquisitionId, row.Kind, row.State });
        operation.Property(row => row.Label).HasMaxLength(200);
        operation.Property(row => row.MediaType).HasMaxLength(100);
        operation.Property(row => row.Extension).HasMaxLength(8);
        operation.Property(row => row.Sha256).HasMaxLength(64);
        operation.Property(row => row.ExpectedItemVersion).HasMaxLength(8);
        operation.Property(row => row.ExpectedAcquisitionVersion).HasMaxLength(8);
        operation.Property(row => row.ExpectedDocumentVersion).HasMaxLength(8);
        operation.Property(row => row.ResultItemVersion).HasMaxLength(8);
        operation.Property(row => row.ResultAcquisitionVersion).HasMaxLength(8);
        operation.Property(row => row.RowVersion).IsRowVersion();
        operation.HasOne<InventoryItem>().WithMany().HasForeignKey(row => new { row.TenantId, row.ItemId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne<Acquisition>().WithMany().HasForeignKey(row => new { row.TenantId, row.AcquisitionId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne<AttachmentRevision>().WithMany().HasForeignKey(row => new { row.TenantId, row.AttachmentId, row.RevisionId }).HasPrincipalKey(row => new { row.TenantId, row.AttachmentId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
