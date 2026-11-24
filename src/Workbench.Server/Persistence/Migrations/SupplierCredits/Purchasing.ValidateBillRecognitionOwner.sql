-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateBillRecognitionOwner
  @TenantId uniqueidentifier,@EventId uniqueidentifier,@Replacement nvarchar(max),@Facts nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Bill source validation requires its owner transaction.',1;
  IF @Replacement IS NOT NULL THROW 51004,'Bill successors require the complete reviewed bill owner.',1;
  SET @Facts=NULL;
  SELECT @Facts=(SELECT bill.Id sourceId,revision.Id sourceRevision,CONVERT(varchar(18),CONVERT(binary(8),bill.RowVersion),1) version,
      review.Id reviewId,CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),revision.Payload)),2) payloadSha256
      FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
    FROM Purchasing.RecognitionSideEvents actual
    JOIN Purchasing.SupplierBillPostingEvents member ON member.TenantId=actual.TenantId AND member.EventId=actual.Id
    JOIN Purchasing.SupplierBillPostings posting ON posting.TenantId=member.TenantId AND posting.BillId=member.BillId
      AND posting.RevisionId=actual.SourceRevision
    JOIN Purchasing.SupplierBills bill WITH(UPDLOCK,HOLDLOCK) ON bill.TenantId=posting.TenantId AND bill.Id=posting.BillId
      AND bill.State='Posted' AND bill.CurrentRevisionId=posting.RevisionId AND bill.CurrentReviewId=posting.ReviewId
    JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=bill.TenantId AND revision.BillId=bill.Id AND revision.Id=posting.RevisionId
    JOIN Purchasing.SupplierBillReviews review ON review.TenantId=bill.TenantId AND review.BillId=bill.Id AND review.Id=posting.ReviewId
      AND review.RevisionId=revision.Id
    WHERE actual.TenantId=@TenantId AND actual.Id=@EventId AND actual.Side='Invoice' AND actual.SourceId=bill.Id
      AND actual.EvidenceMutationPermission=N'SupplierBillsPost'
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected
        WHERE corrected.TenantId=bill.TenantId AND corrected.SourceKind='SupplierBill' AND corrected.SourceId=bill.Id);
  IF @Facts IS NULL THROW 51004,'The current bill source producer does not authorize this recognition correction.',1;
END;
