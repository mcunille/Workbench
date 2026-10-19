-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.ReadSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,@BillId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRANSACTION; EXEC Purchasing.RequireSupplierBillRead @ActorId,@SessionId;
    DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max),@Evidence nvarchar(max);
    IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId) THROW 51004,'Bill unavailable.',1;
    SELECT @Evidence=r.EvidenceJson FROM Purchasing.SupplierBills b LEFT JOIN Purchasing.SupplierBillReviews r ON r.TenantId=b.TenantId AND r.Id=b.CurrentReviewId
      WHERE b.TenantId=@TenantId AND b.Id=@BillId;
    DECLARE @FinancialSet uniqueidentifier=(SELECT Id FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND OwnerKind='SupplierBill' AND OwnerId=@BillId),@FinancialJson nvarchar(max);
EXEC Accounting.BuildFinancialEvidence @TenantId,@FinancialSet,@FinancialJson OUTPUT;
SET @Result=(SELECT JSON_QUERY(@FinancialJson) financialEvidence,b.Id billId,b.PurchaseOrderId purchaseOrderId,b.SupplierId supplierId,b.Currency currency,b.State state,
      b.CurrentRevisionId revisionId,CONVERT(varchar(18),CONVERT(binary(8),b.RowVersion),1) version,r.SupplierName supplierName,JSON_QUERY(r.Payload) revision,
      JSON_QUERY((SELECT v.Id reviewId,v.ActorId actorId,v.Rationale rationale,v.RecordedAtUtc recordedAtUtc,JSON_QUERY(v.Resolutions) resolutions
        FROM Purchasing.SupplierBillReviews v WHERE v.TenantId=b.TenantId AND v.Id=b.CurrentReviewId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) review,
      JSON_QUERY(p.ResultJson) posting,
      JSON_QUERY(COALESCE((SELECT JSON_VALUE(j.value,'$.documentId') documentId,JSON_VALUE(j.value,'$.revisionId') revisionId,
          JSON_VALUE(j.value,'$.digest') digest,CONVERT(bigint,JSON_VALUE(j.value,'$.length')) length,JSON_VALUE(j.value,'$.label') label,
          CONVERT(bit,CASE WHEN d.Id IS NOT NULL AND d.RemovedAtUtc IS NULL AND s.State=1 AND a.Id IS NOT NULL AND a.DeletedAtUtc IS NULL
            AND NOT EXISTS(SELECT 1 FROM Storage.RecoveryFiles f WHERE f.TenantId=@TenantId AND f.RevisionId=s.Id) THEN 1 ELSE 0 END) available
        FROM OPENJSON(@Evidence) j LEFT JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
        LEFT JOIN Storage.Revisions s ON s.TenantId=@TenantId AND s.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))
        LEFT JOIN Storage.Attachments a ON a.TenantId=@TenantId AND a.Id=d.AttachmentId ORDER BY d.Id FOR JSON PATH),N'[]')) evidence
      FROM Purchasing.SupplierBills b JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
        LEFT JOIN Purchasing.SupplierBillPostings p ON p.TenantId=b.TenantId AND p.BillId=b.Id
      WHERE b.TenantId=@TenantId AND b.Id=@BillId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
    COMMIT; SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
