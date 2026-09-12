// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

internal static class ItemPackageSnapshot
{
    internal static async Task<(List<PackageItem> Items, List<PackageDocument> Documents, DateTimeOffset ExportedAt)> CaptureAsync(
        WorkbenchDbContext database, string scope, string providerAlias, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var tenantId = database.TenantContext.RequireTenantId();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var unavailable = database.Database.SqlQuery<Guid>($"SELECT [RevisionId] AS [Value] FROM [Storage].[RecoveryFiles]");
        var rows = await (from item in database.Items.AsNoTracking()
                          where scope == "all" || item.ArchivedAtUtc == null
                          join photoRow in database.ItemPhotos.AsNoTracking() on item.CurrentPhotoId equals photoRow.Id into photos
                          from photo in photos.DefaultIfEmpty()
                          join attachmentRow in database.Attachments.AsNoTracking() on photo.DetailAttachmentId equals attachmentRow.Id into attachments
                          from attachment in attachments.DefaultIfEmpty()
                          join revisionRow in database.AttachmentRevisions.AsNoTracking() on attachment.CurrentRevisionId equals revisionRow.Id into revisions
                          from revision in revisions.DefaultIfEmpty()
                          orderby item.CreatedAtUtc, item.Id
                          select new
                          {
                              Record = new ExportItem(item.Id, item.TrackingKind, item.Name, item.Notes, item.StorageLocation, item.CreatedAtUtc, item.ArchivedAtUtc, null),
                              item.CurrentPhotoId,
                              Photo = photo,
                              Attachment = attachment,
                              Revision = revision,
                              Unavailable = revision != null && unavailable.Contains(revision.Id),
                          }).TagWith("H8 collection package snapshot").Take(ItemExportCsv.MaximumRows + 1).ToListAsync(cancellationToken);
        if (rows.Count > ItemExportCsv.MaximumRows) throw new ItemExportLimitException();
        var acquisitions = await ItemExportAcquisitions.CaptureAsync(database, scope, cancellationToken);
        var documents = await ItemPackageDocuments.CaptureAsync(database, scope, providerAlias, cancellationToken);
        var items = new List<PackageItem>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackagePhoto? photo = null;
            if (row.CurrentPhotoId is not null)
            {
                if (row.Photo is null || row.Attachment is null || row.Revision is null || row.Unavailable ||
                    row.Photo.ItemId != row.Record.Id || row.Photo.TenantId != tenantId ||
                    row.Attachment.TenantId != tenantId || row.Revision.TenantId != tenantId ||
                    row.Revision.AttachmentId != row.Attachment.Id || row.Attachment.DeletedAtUtc is not null ||
                    row.Revision.State != RevisionState.Available || row.Revision.MediaType != "image/webp" ||
                    row.Revision.ProviderAlias != providerAlias || row.Revision.Length is not > 0 ||
                    row.Revision.Sha256 is not { Length: 64 })
                    throw new IOException("Required photograph metadata is unavailable.");
                photo = new(row.Revision.Id, row.Revision.ProviderAlias, row.Revision.Length.Value, row.Revision.Sha256);
            }
            items.Add(new(row.Record with { Acquisition = acquisitions.GetValueOrDefault(row.Record.Id) }, photo));
        }
        var exportedAt = timeProvider.GetUtcNow();
        await transaction.CommitAsync(cancellationToken);
        // Newly retired current attachments retain bytes for seven days, protecting the captured
        // immutable revisions after releasing SQL locks for the two-minute preparation.
        return (items, documents, exportedAt);
    }
}
