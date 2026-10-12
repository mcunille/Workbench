// Copyright (c) 2026 The White Stag Collection.

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

}
