// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceBackfill
{
    internal static void Create(MigrationBuilder migration) => migration.Sql("""
        -- The migrator runs offline, in the release transaction. Never reacquire live evidence or rewrite source bytes.
        DECLARE @Owners TABLE(TenantId uniqueidentifier,Kind varchar(32),Id uniqueidentifier,Revision uniqueidentifier,
          ParentId uniqueidentifier,ConfigurationVersion uniqueidentifier,PRIMARY KEY(TenantId,Kind,Id,Revision));
        INSERT @Owners
          SELECT p.TenantId,'SupplierBill',p.BillId,p.RevisionId,NULL,
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.expectedConfigurationVersion'))
          FROM Purchasing.SupplierBillPostings p JOIN Purchasing.RecognitionGroupReceipts receipt
            ON receipt.TenantId=p.TenantId AND receipt.RequestId=p.RecognitionRequestId;
        INSERT @Owners
          SELECT p.TenantId,'SupplierPayment',p.Id,p.RevisionId,c.OriginalPaymentId,
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.command.expectedConfigurationVersion'))
          FROM Purchasing.SupplierPayments p LEFT JOIN Purchasing.SupplierPaymentCorrections c ON c.TenantId=p.TenantId AND c.GroupId=p.GroupId;
        INSERT @Owners
          SELECT e.TenantId,'PurchaseRecognition',e.Id,e.SourceRevision,old.Id,e.ConfigurationVersion
          FROM Purchasing.RecognitionSideEvents e
          LEFT JOIN Purchasing.RecognitionEventCorrections c ON c.TenantId=e.TenantId AND c.CorrectionGroupId=e.CorrectionGroupId
          LEFT JOIN Purchasing.RecognitionSideEvents old ON old.TenantId=c.TenantId AND old.Id=c.OriginalEventId AND old.Side=e.Side
          WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillRevisions b WHERE b.TenantId=e.TenantId AND b.BillId=e.SourceId AND b.Id=e.SourceRevision);
        DECLARE @Tenant uniqueidentifier,@Kind varchar(32),@Owner uniqueidentifier,@Revision uniqueidentifier,@Config uniqueidentifier,
          @Set uniqueidentifier,@Po uniqueidentifier,@Documents nvarchar(max),@Inherited uniqueidentifier,@Recorded datetimeoffset,@Date date,
          @Diagnostic nvarchar(2048);
        WHILE EXISTS(SELECT 1 FROM @Owners)
        BEGIN
          SET @Owner=NULL;
          SELECT TOP(1) @Tenant=o.TenantId,@Kind=o.Kind,@Owner=o.Id,@Revision=o.Revision,@Config=o.ConfigurationVersion
            FROM @Owners o WHERE o.ParentId IS NULL OR EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceSets s
              WHERE s.TenantId=o.TenantId AND s.OwnerKind=o.Kind AND s.OwnerId=o.ParentId)
            ORDER BY o.TenantId,o.Kind,o.Id;
          IF @Owner IS NULL THROW 51012,'BK-07 upgrade: unresolved or cyclic financial correction ownership; inspect original source identities.',1;
          SET @Diagnostic=N'BK-07 upgrade: inconsistent typed evidence for '+@Kind+N' '+CONVERT(nvarchar(36),@Owner)
            +N' in tenant '+CONVERT(nvarchar(36),@Tenant)+N'. Inspect the immutable source document/revision, digest, length and configuration identity; no changes were applied.';
          SET @Po=NULL;
          SELECT @Po=PurchaseOrderId,@Documents=Documents,@Inherited=InheritedEvidenceSetId,@Recorded=RecordedAtUtc,@Date=PostingDate
            FROM Accounting.FinancialEvidenceSource(@Tenant,@Kind,@Owner,@Revision);
          IF @Po IS NULL OR @Config IS NULL THROW 51012,@Diagnostic,1;
          IF @Documents IS NOT NULL AND ISJSON(@Documents,ARRAY)<>1 THROW 51012,@Diagnostic,1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Documents) j
            LEFT JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@Tenant AND d.OrderId=@Po
              AND d.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
              AND d.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))
            LEFT JOIN Storage.Revisions r ON r.TenantId=d.TenantId AND r.Id=d.RevisionId AND r.AttachmentId=d.AttachmentId
            WHERE j.type<>5 OR d.Id IS NULL OR r.Id IS NULL OR r.State=0 OR r.Sha256 IS NULL OR r.Length IS NULL
              OR d.Sha256<>r.Sha256 OR d.Length<>r.Length OR d.MediaType<>r.MediaType
              OR JSON_VALUE(j.value,'$.digest') IS NULL OR JSON_VALUE(j.value,'$.digest')<>r.Sha256
              OR TRY_CONVERT(bigint,JSON_VALUE(j.value,'$.length')) IS NULL OR TRY_CONVERT(bigint,JSON_VALUE(j.value,'$.length'))<>r.Length
              OR NULLIF(JSON_VALUE(j.value,'$.label'),'') IS NULL)
            THROW 51012,@Diagnostic,1;
          IF EXISTS(SELECT JSON_VALUE(value,'$.documentId') FROM OPENJSON(@Documents) GROUP BY JSON_VALUE(value,'$.documentId') HAVING COUNT(*)>1)
            THROW 51012,@Diagnostic,1;
          SET @Set=NEWID();
          INSERT Accounting.FinancialEvidenceSets(TenantId,Id,OwnerKind,OwnerId,OwnerRevisionId,PurchaseOrderId,SupplierId,ActorId,
            PostingDate,RecordedAtUtc,SourceSnapshotSha256,MissingEvidenceReason,LegacyEvidence,MutationPermission,InheritedEvidenceSetId)
            SELECT @Tenant,@Set,@Kind,@Owner,@Revision,PurchaseOrderId,SupplierId,ActorId,PostingDate,RecordedAtUtc,SourceSnapshotSha256,
              CASE WHEN NOT EXISTS(SELECT 1 FROM OPENJSON(@Documents)) THEN COALESCE(NULLIF(MissingEvidenceReason,''),
                N'Legacy source has no authenticated document evidence; historical references remain unresolved.') ELSE MissingEvidenceReason END,
              1,MutationPermission,InheritedEvidenceSetId FROM Accounting.FinancialEvidenceSource(@Tenant,@Kind,@Owner,@Revision);
          INSERT Accounting.FinancialEvidenceLinks(TenantId,Id,EvidenceSetId,DocumentId,AttachmentId,RevisionId,Sha256,Length,Label,
            MediaType,Extension,ConfigurationVersion,RetentionYears,RetentionRationale,AnchorAtUtc,MinimumRetentionDeadlineUtc,RecordedAtUtc)
            SELECT @Tenant,NEWID(),@Set,d.Id,d.AttachmentId,d.RevisionId,r.Sha256,r.Length,JSON_VALUE(j.value,'$.label'),
              d.MediaType,d.Extension,@Config,NULL,N'Legacy evidence: original retention policy was not enforced; indefinite protection.',
              CASE WHEN CONVERT(datetimeoffset,@Date)>@Recorded THEN CONVERT(datetimeoffset,@Date) ELSE @Recorded END,NULL,@Recorded
            FROM OPENJSON(@Documents) j JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@Tenant AND d.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
            JOIN Storage.Revisions r ON r.TenantId=d.TenantId AND r.Id=d.RevisionId AND r.AttachmentId=d.AttachmentId
            WHERE NOT EXISTS(SELECT 1 FROM Accounting.InheritedFinancialEvidence(@Tenant,@Inherited) old WHERE old.DocumentId=d.Id AND old.RevisionId=d.RevisionId);
          DELETE @Owners WHERE TenantId=@Tenant AND Kind=@Kind AND Id=@Owner AND Revision=@Revision;
        END;
        UPDATE a SET Held=1 FROM Storage.Attachments a WHERE EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks l
          WHERE l.TenantId=a.TenantId AND l.AttachmentId=a.Id);
        INSERT Storage.FinancialEvidenceAttachmentStates(TenantId,AttachmentId)
          SELECT DISTINCT TenantId,AttachmentId FROM Accounting.FinancialEvidenceLinks;
        -- SQL cannot establish surviving bytes. Require explicit offline verification before resuming writers/workers.
        IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks)
          UPDATE Security.BlobRecoveryState SET IsPending=1 WHERE Id=1;
        """);
}
