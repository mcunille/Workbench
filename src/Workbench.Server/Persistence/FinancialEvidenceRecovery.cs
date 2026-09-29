// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceRecovery
{
    internal static void Create(MigrationBuilder migration)
    {
        // Logical disposal is durable authority; elapsed grace controls physical deletion independently.
        migration.Sql("""
            CREATE FUNCTION Storage.FinanciallyProtectedRevision(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              RETURN CASE WHEN EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks
                WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId) THEN 1 ELSE 0 END;
            END;
            """);
        FinancialEvidenceSchema.Alter(migration, "Storage.ReadRecoveryInventory",
            ("DECLARE @Rows nvarchar(max)", """
                DECLARE @Evidence nvarchar(max)=(SELECT
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceSets WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Sets,
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceLinks WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Links,
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceAdditions WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Additions,
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceReceipts WITH(HOLDLOCK) ORDER BY TenantId,RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)) Receipts,
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceDisposals WITH(HOLDLOCK) ORDER BY TenantId,RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)) Disposals,
                  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceDisposalLinks WITH(HOLDLOCK) ORDER BY TenantId,RequestId,LinkId FOR JSON PATH,INCLUDE_NULL_VALUES)) DisposalLinks,
                  JSON_QUERY((SELECT * FROM Storage.FinancialEvidenceAttachmentStates WITH(HOLDLOCK) ORDER BY TenantId,AttachmentId FOR JSON PATH)) AttachmentVersions,
                  JSON_QUERY((SELECT a.TenantId,a.Id,a.Held,a.IndependentHeld,a.CurrentRevisionId,a.DeletedAtUtc,a.DeleteAfterUtc
                    FROM Storage.Attachments a WITH(HOLDLOCK) WHERE Storage.FinanciallyProtectedRevision(a.TenantId,a.Id)=1
                    ORDER BY a.TenantId,a.Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Attachments,
                  JSON_QUERY((SELECT d.TenantId,d.Id,d.AttachmentId,d.RevisionId,d.RemovedAtUtc
                    FROM Purchasing.PurchaseOrderDocuments d WITH(HOLDLOCK) WHERE Storage.FinanciallyProtectedRevision(d.TenantId,d.AttachmentId)=1
                    ORDER BY d.TenantId,d.Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Documents
                  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
                DECLARE @EvidenceFingerprint varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@Evidence),2);
                DECLARE @Rows nvarchar(max)
                """),
            ("[Length], Sha256, [State], RowVersion", """
                [Length], Sha256, [State], RowVersion,
                Storage.FinanciallyProtectedRevision(TenantId,AttachmentId) FinanciallyProtected,
                CONVERT(bit,CASE WHEN Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1
                  AND Storage.FinancialEvidenceDeletionAuthorized(TenantId,AttachmentId)=1 THEN 1 ELSE 0 END) Disposed
                """),
            ("@Generation AS Generation,", "@Generation AS Generation, @EvidenceFingerprint AS FinancialEvidenceFingerprint,"));
        FinancialEvidenceSchema.Alter(migration, "Storage.ExportManifest",
            ("WHERE [State] = 1 AND", "WHERE ([State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) AND"),
            ("AND (@AfterId", "AND NOT (Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1 AND Storage.FinancialEvidenceDeletionAuthorized(TenantId,AttachmentId)=1) AND (@AfterId"));
        FinancialEvidenceSchema.Alter(migration, "Storage.RelocateRevision",
            ("AND [State] = 1 AND", "AND ([State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) AND"));
        FinancialEvidenceSchema.Alter(migration, "Storage.AcceptFileRecovery",
            ("r.State<>1", "(r.State<>1 AND Storage.FinanciallyProtectedRevision(r.TenantId,r.AttachmentId)=0) OR (Storage.FinanciallyProtectedRevision(r.TenantId,r.AttachmentId)=1 AND Storage.FinancialEvidenceDeletionAuthorized(r.TenantId,r.AttachmentId)=1)"),
            ("SET ProviderAlias=@TargetAlias WHERE State=1", "SET ProviderAlias=@TargetAlias WHERE State=1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1"));
        FinancialEvidenceSchema.Alter(migration, "Administration.SanitizeRestore",
            ("WHERE [State] = 1) THEN 1 ELSE 0 END", "WHERE [State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) THEN 1 ELSE 0 END"));
    }
}
