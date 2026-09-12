// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

public sealed record PackageDocument(Guid Id, Guid AcquisitionId, string Label, DateTimeOffset CreatedAtUtc,
    string MediaType, string Extension, Guid RevisionId, string ProviderAlias, long Length, string Sha256)
{
    public string Path => $"documents/{AcquisitionId:D}/{Id:D}.{Extension}";
}

internal static class ItemPackageDocuments
{
    internal static async Task<List<PackageDocument>> CaptureAsync(WorkbenchDbContext database,
        string scope, string providerAlias, CancellationToken cancellationToken)
    {
        var tenantId = database.TenantContext.RequireTenantId();
        var selected = from item in database.Items
                       where scope == "all" || item.ArchivedAtUtc == null
                       join link in database.AcquisitionItems on item.Id equals link.ItemId
                       select link.AcquisitionId;
        var unavailable = database.Database.SqlQuery<Guid>($"SELECT [RevisionId] AS [Value] FROM [Storage].[RecoveryFiles]");
        var rows = await (from document in database.AcquisitionDocuments.AsNoTracking()
                          where document.RemovedAtUtc == null && selected.Contains(document.AcquisitionId)
                          join attachmentRow in database.Attachments.AsNoTracking() on document.AttachmentId equals attachmentRow.Id into attachments
                          from attachment in attachments.DefaultIfEmpty()
                          join revisionRow in database.AttachmentRevisions.AsNoTracking() on document.RevisionId equals revisionRow.Id into revisions
                          from revision in revisions.DefaultIfEmpty()
                          orderby document.AcquisitionId, document.CreatedAtUtc, document.Id
                          select new
                          {
                              Document = document,
                              Attachment = attachment,
                              Revision = revision,
                              Unavailable = revision != null && unavailable.Contains(revision.Id)
                          })
            .TagWith("H12 document snapshot").Take(ItemPackageArchive.MaximumDocuments + 1).ToListAsync(cancellationToken);
        if (rows.Count > ItemPackageArchive.MaximumDocuments) throw new ItemExportLimitException();
        var documents = new List<PackageDocument>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = row.Document;
            if (row.Attachment is null || row.Revision is null || row.Unavailable ||
                document.TenantId != tenantId || row.Attachment.TenantId != tenantId || row.Revision.TenantId != tenantId ||
                row.Attachment.DeletedAtUtc is not null || row.Attachment.CurrentRevisionId != document.RevisionId ||
                row.Revision.AttachmentId != document.AttachmentId || row.Revision.State != RevisionState.Available ||
                row.Revision.ProviderAlias != providerAlias || row.Revision.MediaType != document.MediaType ||
                row.Revision.Length != document.Length || !string.Equals(row.Revision.Sha256, document.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Required document metadata is unavailable.");
            documents.Add(new(document.Id, document.AcquisitionId, document.Label, document.CreatedAtUtc,
                document.MediaType, document.Extension, document.RevisionId, row.Revision.ProviderAlias, document.Length, document.Sha256));
        }
        return documents;
    }

    internal static bool ValidType(PackageDocument document) => (document.MediaType, document.Extension) is
        ("application/pdf", "pdf") or ("image/jpeg", "jpg") or ("image/png", "png") or ("image/webp", "webp");
}
