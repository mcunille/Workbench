// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class AcquisitionDocumentFixture
{
    internal sealed record Saved(Guid ItemId, Guid AcquisitionId, Guid DocumentId, Guid AttachmentId, AttachmentRevisionInfo Revision, byte[] Bytes);
    internal static async Task<Saved> UploadAsync(string admin, string web, TenantContextProof proof, Guid tenant, IBlobStore store)
    {
        var item = Guid.NewGuid(); var acquisition = Guid.NewGuid();
        await using (var connection = new SqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT Inventory.Items(Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
                VALUES(@item,@tenant,'Individual','Recovery specimen',SYSUTCDATETIME(),NEWID());
                INSERT Inventory.Acquisitions(Id,TenantId,Method,CreatedAtUtc,CreationRequestId)
                VALUES(@acquisition,@tenant,'Unknown',SYSUTCDATETIME(),NEWID());
                INSERT Inventory.AcquisitionItems(TenantId,ItemId,AcquisitionId) VALUES(@tenant,@item,@acquisition);
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenant); command.Parameters.AddWithValue("@item", item); command.Parameters.AddWithValue("@acquisition", acquisition);
            await command.ExecuteNonQueryAsync();
        }
        await using var context = BlobPersistenceTests.CreateContext(web, proof, tenant);
        var actor = new RequestActor(Guid.NewGuid(), tenant, Guid.NewGuid(), new HashSet<string>());
        var itemVersion = (await context.Items.SingleAsync(row => row.Id == item)).RowVersion;
        var acquisitionVersion = (await context.Acquisitions.SingleAsync(row => row.Id == acquisition)).RowVersion;
        var bytes = PhotoFixture.Png();
        var operation = await new AcquisitionDocumentService(context, store, actor).ChangeAsync(item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion,
            null, null, "Recovery receipt", bytes, new ValidatedDocument("image/png", "png"), default);
        var document = await context.AcquisitionDocuments.SingleAsync(row => row.Id == operation.DocumentId);
        return new(item, acquisition, document.Id, document.AttachmentId, new(document.RevisionId, document.Length, document.Sha256), bytes);
    }
}
