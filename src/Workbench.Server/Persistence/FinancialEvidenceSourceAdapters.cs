// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceSourceAdapters
{
    internal const string SourceSql = """
        CREATE FUNCTION Accounting.FinancialEvidenceSource(@TenantId uniqueidentifier,@OwnerKind varchar(32),@OwnerId uniqueidentifier,@OwnerRevisionId uniqueidentifier)
        RETURNS TABLE AS RETURN
          SELECT b.PurchaseOrderId,b.SupplierId,receipt.ActorId,CONVERT(date,JSON_VALUE(r.Payload,'$.postingDate')) PostingDate,p.RecordedAtUtc,
            HASHBYTES('SHA2_256',CONVERT(varbinary(max),r.Payload)) SourceSnapshotSha256,
            review.EvidenceJson Documents,JSON_VALUE(r.Payload,'$.missingEvidenceReason') MissingEvidenceReason,
            CONVERT(bit,0) LegacyEvidence,CONVERT(nvarchar(100),N'SupplierBillsManage') MutationPermission,CONVERT(uniqueidentifier,NULL) InheritedEvidenceSetId
          FROM Purchasing.SupplierBillPostings p JOIN Purchasing.SupplierBills b ON b.TenantId=p.TenantId AND b.Id=p.BillId
          JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=p.TenantId AND r.BillId=p.BillId AND r.Id=p.RevisionId
          JOIN Purchasing.SupplierBillReviews review ON review.TenantId=p.TenantId AND review.Id=p.ReviewId AND review.RevisionId=p.RevisionId
          JOIN Purchasing.RecognitionGroupReceipts receipt ON receipt.TenantId=p.TenantId AND receipt.RequestId=p.RecognitionRequestId
          WHERE @OwnerKind='SupplierBill' AND p.TenantId=@TenantId AND p.BillId=@OwnerId AND p.RevisionId=@OwnerRevisionId
          UNION ALL
          SELECT p.PurchaseOrderId,p.SupplierId,p.ActorId,p.PostingDate,p.RecordedAtUtc,HASHBYTES('SHA2_256',CONVERT(varbinary(max),p.EvidenceJson)),
            JSON_QUERY(p.EvidenceJson,'$.evidence.documents'),JSON_VALUE(p.EvidenceJson,'$.evidence.missingEvidenceReason'),CONVERT(bit,0),
            CASE WHEN EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections c WHERE c.TenantId=p.TenantId AND c.GroupId=p.GroupId) THEN N'SupplierPaymentsCorrect' ELSE N'SupplierPaymentsRecord' END,
            (SELECT original.Id FROM Purchasing.SupplierPaymentCorrections c JOIN Accounting.FinancialEvidenceSets original
              ON original.TenantId=c.TenantId AND original.OwnerKind='SupplierPayment' AND original.OwnerId=c.OriginalPaymentId
              WHERE c.TenantId=p.TenantId AND c.GroupId=p.GroupId)
          FROM Purchasing.SupplierPayments p WHERE @OwnerKind='SupplierPayment' AND p.TenantId=@TenantId AND p.Id=@OwnerId AND p.RevisionId=@OwnerRevisionId
          UNION ALL
          SELECT u.PurchaseOrderId,u.SupplierId,e.ActorId,e.PostingDate,e.RecordedAtUtc,e.EvidenceSha256,
            JSON_QUERY(e.EvidenceJson,'$.documents'),COALESCE(JSON_VALUE(e.EvidenceJson,'$.missingEvidenceReason'),
              CASE WHEN JSON_QUERY(e.EvidenceJson,'$.documents') IS NULL THEN JSON_VALUE(e.EvidenceJson,'$.rationale') END),
            CONVERT(bit,CASE WHEN JSON_QUERY(e.EvidenceJson,'$.documents') IS NULL THEN 1 ELSE 0 END),e.EvidenceMutationPermission,
            (SELECT original.Id FROM Purchasing.RecognitionEventCorrections c JOIN Purchasing.RecognitionSideEvents old
              ON old.TenantId=c.TenantId AND old.Id=c.OriginalEventId AND old.Side=e.Side
              JOIN Accounting.FinancialEvidenceSets original ON original.TenantId=old.TenantId AND original.OwnerKind='PurchaseRecognition' AND original.OwnerId=old.Id
              WHERE c.TenantId=e.TenantId AND c.CorrectionGroupId=e.CorrectionGroupId)
          FROM Purchasing.RecognitionSideEvents e JOIN Purchasing.RecognitionUnits u ON u.TenantId=e.TenantId AND u.Id=e.UnitId
          WHERE @OwnerKind='PurchaseRecognition' AND e.TenantId=@TenantId AND e.Id=@OwnerId AND e.SourceRevision=@OwnerRevisionId
            AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillRevisions b WHERE b.TenantId=e.TenantId AND b.BillId=e.SourceId AND b.Id=e.SourceRevision);
        """;

    // The PO row is the existing document coordination lock. Acquire each attachment before its revision,
    // in stable attachment order; never rely on optimizer join order for cleanup compatibility.
    internal const string ValidateSql = """
        ALTER PROCEDURE Purchasing.ValidateBillEvidence @TenantId uniqueidentifier,@PurchaseOrderId uniqueidentifier,
          @Payload nvarchar(max),@Evidence nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 THROW 51000,'Source transaction required.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents')) AND
            (EXISTS(SELECT 1 FROM Security.BlobRecoveryState WHERE IsPending=1) OR EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1))
            THROW 51004,'Evidence recovery verification is pending.',1;
          IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
            WHERE TenantId=@TenantId AND Id=@PurchaseOrderId AND IsDeleted=0 AND State='Ordered') THROW 51004,'Evidence purchase unavailable.',1;
          IF EXISTS(SELECT JSON_VALUE(value,'$.documentId') FROM OPENJSON(@Payload,'$.documents') GROUP BY JSON_VALUE(value,'$.documentId') HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents') WHERE type<>5
              OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.documentId')) IS NULL OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.revisionId')) IS NULL)
            THROW 51000,'Invalid evidence document identities.',1;
          DECLARE @Documents TABLE(DocumentId uniqueidentifier PRIMARY KEY,AttachmentId uniqueidentifier,RevisionId uniqueidentifier);
          INSERT @Documents SELECT d.Id,d.AttachmentId,d.RevisionId FROM OPENJSON(@Payload,'$.documents') j
            JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.OrderId=@PurchaseOrderId AND d.RemovedAtUtc IS NULL
              AND d.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) AND d.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'));
          IF (SELECT COUNT(*) FROM @Documents)<>(SELECT COUNT(*) FROM OPENJSON(@Payload,'$.documents')) THROW 51004,'Evidence document unavailable.',1;
          DECLARE @Document uniqueidentifier,@Attachment uniqueidentifier,@Revision uniqueidentifier,@Digest varchar(64),@Length bigint,@Media nvarchar(100);
          DECLARE evidence_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT DocumentId,AttachmentId,RevisionId FROM @Documents ORDER BY AttachmentId,RevisionId,DocumentId;
          OPEN evidence_cursor; FETCH NEXT FROM evidence_cursor INTO @Document,@Attachment,@Revision;
          WHILE @@FETCH_STATUS=0
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Storage.Attachments WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Attachment
              AND CurrentRevisionId=@Revision AND DeletedAtUtc IS NULL) THROW 51004,'Evidence attachment unavailable.',1;
            SET @Digest=NULL;
            SELECT @Digest=Sha256,@Length=Length,@Media=MediaType FROM Storage.Revisions WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@TenantId AND AttachmentId=@Attachment AND Id=@Revision AND State=1;
            IF @Digest IS NULL OR NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@Document
                AND Sha256=@Digest AND Length=@Length AND MediaType=@Media)
              OR EXISTS(SELECT 1 FROM Storage.RecoveryFiles WHERE TenantId=@TenantId AND RevisionId=@Revision)
              THROW 51004,'Evidence revision unavailable or inconsistent.',1;
            FETCH NEXT FROM evidence_cursor INTO @Document,@Attachment,@Revision;
          END;
          CLOSE evidence_cursor; DEALLOCATE evidence_cursor;
          SET @Evidence=(SELECT d.Id documentId,d.RevisionId revisionId,d.Sha256 digest,d.Length length,d.Label label
            FROM @Documents selected JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=selected.DocumentId ORDER BY d.Id FOR JSON PATH);
          SET @Evidence=COALESCE(@Evidence,N'[]');
        END;
        """;

    internal static void InstallHooks(MigrationBuilder migration)
    {
        FinancialEvidenceSchema.Alter(migration, "Purchasing.PlanSupplierCorrection", ("EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@EvidenceInput,@ReplacementDocuments OUTPUT;", """
            DECLARE @InheritedEvidence uniqueidentifier=(SELECT Id FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND OwnerKind='SupplierPayment' AND OwnerId=@Payment);
            EXEC Accounting.ValidateInheritedFinancialEvidence @TenantId,@Po,@EvidenceInput,@InheritedEvidence,@ReplacementDocuments OUTPUT;
            """));
        FinancialEvidenceSchema.Alter(migration, "Purchasing.RecordSupplierPaymentCore", ("EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@EvidenceInput,@Documents OUTPUT;", """
            DECLARE @InheritedEvidence uniqueidentifier=(SELECT original.Id FROM Purchasing.SupplierPaymentCorrections c JOIN Accounting.FinancialEvidenceSets original
              ON original.TenantId=c.TenantId AND original.OwnerKind='SupplierPayment' AND original.OwnerId=c.OriginalPaymentId WHERE c.TenantId=@TenantId AND c.GroupId=@Group);
            EXEC Accounting.ValidateInheritedFinancialEvidence @TenantId,@Po,@EvidenceInput,@InheritedEvidence,@Documents OUTPUT;
            """));
        FinancialEvidenceSchema.Alter(migration, "Purchasing.PostSupplierBill", ("INSERT Purchasing.SupplierBillReceipts", """
            DECLARE @FinancialEvidenceSet uniqueidentifier;
            EXEC Accounting.CaptureFinancialEvidence 'SupplierBill',@BillId,@RevisionId,@FinancialEvidenceSet OUTPUT;
            INSERT Purchasing.SupplierBillReceipts
            """));
        FinancialEvidenceSchema.Alter(migration, "Purchasing.RecordSupplierPaymentCore", ("INSERT Purchasing.SupplierFinancialReceipts", """
            DECLARE @FinancialEvidenceSet uniqueidentifier;
            EXEC Accounting.CaptureFinancialEvidence 'SupplierPayment',@Payment,@Revision,@FinancialEvidenceSet OUTPUT;
            INSERT Purchasing.SupplierFinancialReceipts
            """));
        FinancialEvidenceSchema.Alter(migration, "Purchasing.PostRecognition",
            ("ActorId,EvidenceJson,EvidenceSha256,JournalId", "ActorId,EvidenceMutationPermission,EvidenceJson,EvidenceSha256,JournalId"),
            ("@ConfigurationVersion,@ActorId,@Evidence,HASHBYTES", "@ConfigurationVersion,@ActorId,@RequiredPermission,@Evidence,HASHBYTES"),
            ("INSERT @Events VALUES(@EventId,@UnitId,@JournalId);", """
                IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillRevisions WHERE TenantId=@TenantId AND BillId=@SourceId AND Id=@SourceRevision)
                BEGIN
                  DECLARE @FinancialEvidenceSet uniqueidentifier;
                  EXEC Accounting.CaptureFinancialEvidence 'PurchaseRecognition',@EventId,@SourceRevision,@FinancialEvidenceSet OUTPUT;
                END;
                INSERT @Events VALUES(@EventId,@UnitId,@JournalId);
                """));
        FinancialEvidenceSchema.Alter(migration, "Purchasing.CanonicalizeRecognition",
            ("('evidence','documentRevision',1,0,400)", "('evidence','documents',4,0,262144),('evidence','missingEvidenceReason',1,0,4000),('document','documentId',1,1,72),('document','revisionId',1,1,72),('evidence','documentRevision',1,0,400)"),
            ("IF @Depth>7", "IF @Depth>9"),
            ("WHEN n.Kind='units' OR", "WHEN n.Kind='documents' THEN 'document' WHEN n.Kind='units' OR"),
            ("'units','sides','components','evidence','expectedEventRevisions'", "'units','sides','components','evidence','expectedEventRevisions','documents'"),
            ("'unitId','purchaseOrderId','supplierId','sourceId','sourceRevision','expectedConfigurationVersion'", "'unitId','purchaseOrderId','supplierId','sourceId','sourceRevision','expectedConfigurationVersion','documentId','revisionId'"));
    }
}
