-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierBillCorrectionSource
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Source nvarchar(max) OUTPUT,@Units nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Bill planning requires its owner transaction.',1;
  DECLARE @Bill uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Revision uniqueidentifier,@Payload nvarchar(max),
    @Version binary(8),@Review uniqueidentifier,@Posting date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
    @Replacement nvarchar(max)=JSON_QUERY(@Canonical,'$.replacement'),@Successor uniqueidentifier,@SuccessorRevision uniqueidentifier,
    @SuccessorPayload nvarchar(max),@Post nvarchar(max),@Evidence nvarchar(max),@Resolutions nvarchar(max);
  SELECT @Version=bill.RowVersion,@Revision=posting.RevisionId,@Review=posting.ReviewId,@Payload=revision.Payload
    FROM Purchasing.SupplierBills bill WITH(UPDLOCK,HOLDLOCK)
    JOIN Purchasing.SupplierBillPostings posting ON posting.TenantId=bill.TenantId AND posting.BillId=bill.Id
    JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=posting.TenantId AND revision.BillId=bill.Id AND revision.Id=posting.RevisionId
    JOIN Purchasing.SupplierBillReviews review ON review.TenantId=posting.TenantId AND review.Id=posting.ReviewId
      AND review.BillId=bill.Id AND review.RevisionId=revision.Id
    WHERE bill.TenantId=@TenantId AND bill.Id=@Bill AND bill.State='Posted' AND bill.CurrentRevisionId=posting.RevisionId
      AND bill.CurrentReviewId=posting.ReviewId AND bill.PurchaseOrderId=@Po AND bill.SupplierId=@Supplier AND bill.Currency=@Currency;
  IF @Version IS NULL OR @Version<>CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedSourceVersion'),1)
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections WHERE TenantId=@TenantId AND SourceKind='SupplierBill' AND SourceId=@Bill)
    THROW 51009,'The posted bill revision changed or was already corrected.',1;
  IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item JOIN Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK)
      ON version.TenantId=item.TenantId AND version.ItemId=item.Id
    CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) control
    WHERE item.TenantId=@TenantId AND item.Id=@Bill AND item.BillId=@Bill AND item.SourceRevisionId=@Revision
      AND item.Kind='Payable' AND item.SourceKind='SupplierBill' AND control.AccountPurpose='SupplierPayable')
    THROW 51004,'Complete authentic bill control evidence is required.',1;
  IF @Posting<CONVERT(date,JSON_VALUE(@Payload,'$.postingDate'),23)
    THROW 51004,'A bill correction cannot predate the original posting.',1;
  DECLARE @Original TABLE(UnitId uniqueidentifier,EventId uniqueidentifier PRIMARY KEY,Side varchar(16),EventRevision int,
    JournalId uniqueidentifier,PostingDate date,EvidenceHash varchar(64));
  INSERT @Original SELECT actual.UnitId,actual.Id,actual.Side,actual.EventRevision,actual.JournalId,actual.PostingDate,
      CONVERT(varchar(64),actual.EvidenceSha256,2)
    FROM Purchasing.SupplierBillPostingEvents owned
    JOIN Purchasing.RecognitionSideEvents invoice ON invoice.TenantId=owned.TenantId AND invoice.Id=owned.EventId AND invoice.Side='Invoice'
    JOIN Purchasing.ActiveRecognitionSideEvents actual WITH(UPDLOCK,HOLDLOCK) ON actual.TenantId=invoice.TenantId AND actual.UnitId=invoice.UnitId
    WHERE owned.TenantId=@TenantId AND owned.BillId=@Bill;
  IF NOT EXISTS(SELECT 1 FROM @Original) OR (SELECT COUNT(*) FROM @Original WHERE Side='Invoice')<>(SELECT COUNT(*) FROM OPENJSON(@Payload,'$.units'))
    OR EXISTS(SELECT 1 FROM @Original original JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=@TenantId AND actual.Id=original.EventId
      WHERE actual.EvidenceSha256<>HASHBYTES('SHA2_256',CONVERT(varbinary(max),actual.EvidenceJson))
        OR actual.PostingDate>@Posting OR EXISTS(SELECT 1 FROM Purchasing.RecognitionEventCorrections corrected
          WHERE corrected.TenantId=actual.TenantId AND corrected.OriginalEventId=actual.Id))
    THROW 51004,'The complete active bill recognition group is unavailable.',1;
  IF @Replacement IS NOT NULL
  BEGIN
    IF (SELECT COUNT(*) FROM OPENJSON(@Replacement))<>2 OR EXISTS(SELECT 1 FROM OPENJSON(@Replacement)
      WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('post','unitBindings') OR ([key]='post' AND type<>5) OR ([key]='unitBindings' AND type<>4))
      THROW 51000,'A bill successor requires its reviewed post command and explicit unit bindings.',1;
    SET @Post=JSON_QUERY(@Replacement,'$.post');
    EXEC Purchasing.CanonicalizeSupplierBill @Command=@Post,@Canonical=@Post OUTPUT;
    IF JSON_VALUE(@Post,'$.operation')<>'Post' OR JSON_VALUE(@Post,'$.expectedConfigurationVersion')<>JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')
      OR JSON_VALUE(@Post,'$.expectedPurchaseOrderVersion')<>JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion')
      OR (SELECT COUNT(*) FROM OPENJSON(@Post))<>7
      THROW 51000,'The successor must use the actual reviewed bill posting contract.',1;
    SET @Successor=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Post,'$.billId'));
    SET @SuccessorRevision=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Post,'$.revisionId'));
    SELECT @SuccessorPayload=revision.Payload,@Resolutions=review.Resolutions FROM Purchasing.SupplierBills bill WITH(UPDLOCK,HOLDLOCK)
      JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=bill.TenantId AND revision.BillId=bill.Id AND revision.Id=bill.CurrentRevisionId
      JOIN Purchasing.SupplierBillReviews review ON review.TenantId=bill.TenantId AND review.Id=bill.CurrentReviewId
        AND review.BillId=bill.Id AND review.RevisionId=revision.Id
      WHERE bill.TenantId=@TenantId AND bill.Id=@Successor AND bill.Id<>@Bill AND bill.State='Reviewed' AND revision.Id=@SuccessorRevision
        AND bill.PurchaseOrderId=@Po AND bill.SupplierId=@Supplier AND bill.Currency=@Currency
        AND bill.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(@Post,'$.billVersion'),1)
        AND review.PurchaseOrderVersion=TRY_CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1);
    IF @SuccessorPayload IS NULL OR EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems WHERE TenantId=@TenantId AND Id=@Successor)
      THROW 51009,'The fresh reviewed successor bill changed or collides with an existing source.',1;
    EXEC Purchasing.ValidateReviewedBill @TenantId,@Successor,@SuccessorRevision,@Resolutions,@Evidence OUTPUT;
    IF JSON_VALUE(@SuccessorPayload,'$.kind')<>'Invoice'
      OR JSON_VALUE(@SuccessorPayload,'$.postingDate')<>JSON_VALUE(@Canonical,'$.postingDate')
      OR JSON_VALUE(@SuccessorPayload,'$.effectiveDate')<>JSON_VALUE(@Payload,'$.effectiveDate')
      OR (SELECT COUNT(*) FROM OPENJSON(@SuccessorPayload,'$.units'))<>(SELECT COUNT(*) FROM OPENJSON(@Payload,'$.units'))
      OR (SELECT COUNT(*) FROM OPENJSON(@Replacement,'$.unitBindings'))<>(SELECT COUNT(*) FROM OPENJSON(@Payload,'$.units'))
      THROW 51009,'The successor must preserve complete original whole units and effective dates.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Replacement,'$.unitBindings') binding
      WHERE binding.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(binding.value))<>4 OR EXISTS(SELECT 1 FROM OPENJSON(binding.value) field
        WHERE field.[key] COLLATE Latin1_General_100_BIN2 NOT IN('originalUnitId','successorUnitId','invoiceEventId','correctionGroupId')
          OR field.type<>1 OR DATALENGTH(field.value)<>72 OR TRY_CONVERT(uniqueidentifier,field.value) IS NULL
          OR TRY_CONVERT(uniqueidentifier,field.value)='00000000-0000-0000-0000-000000000000')
      OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.units') original JOIN OPENJSON(@SuccessorPayload,'$.units') successor
          ON JSON_VALUE(successor.value,'$.componentKey') COLLATE Latin1_General_100_BIN2=JSON_VALUE(original.value,'$.componentKey')
        WHERE JSON_VALUE(original.value,'$.unitId')=JSON_VALUE(binding.value,'$.originalUnitId')
          AND JSON_VALUE(successor.value,'$.unitId')=JSON_VALUE(binding.value,'$.successorUnitId')
          AND JSON_VALUE(successor.value,'$.classification')=JSON_VALUE(original.value,'$.classification')
          AND JSON_VALUE(successor.value,'$.goodsReference') COLLATE Latin1_General_100_BIN2=JSON_VALUE(original.value,'$.goodsReference')
          AND JSON_VALUE(successor.value,'$.quantityUnit') COLLATE Latin1_General_100_BIN2=JSON_VALUE(original.value,'$.quantityUnit')
          AND TRY_CONVERT(decimal(28,6),JSON_VALUE(successor.value,'$.quantity'))=TRY_CONVERT(decimal(28,6),JSON_VALUE(original.value,'$.quantity'))
          AND TRY_CONVERT(int,JSON_VALUE(successor.value,'$.expectedPriorEventRevision'))=0)
      OR EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.successorUnitId')))
      OR EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.invoiceEventId')))
      OR EXISTS(SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.correctionGroupId'))))
      OR EXISTS(SELECT JSON_VALUE(value,'$.originalUnitId') FROM OPENJSON(@Replacement,'$.unitBindings') GROUP BY JSON_VALUE(value,'$.originalUnitId') HAVING COUNT(*)>1)
      OR EXISTS(SELECT JSON_VALUE(value,'$.successorUnitId') FROM OPENJSON(@Replacement,'$.unitBindings') GROUP BY JSON_VALUE(value,'$.successorUnitId') HAVING COUNT(*)>1)
      OR EXISTS(SELECT JSON_VALUE(value,'$.invoiceEventId') FROM OPENJSON(@Replacement,'$.unitBindings') GROUP BY JSON_VALUE(value,'$.invoiceEventId') HAVING COUNT(*)>1)
      OR EXISTS(SELECT JSON_VALUE(value,'$.correctionGroupId') FROM OPENJSON(@Replacement,'$.unitBindings') GROUP BY JSON_VALUE(value,'$.correctionGroupId') HAVING COUNT(*)>1)
      THROW 51009,'The complete fresh successor recognition bindings are required.',1;
  END;
  DECLARE @Bindings nvarchar(max)=JSON_QUERY(@Replacement,'$.unitBindings');
  IF @Replacement IS NULL
  BEGIN
    SET @Bindings=(SELECT original.UnitId originalUnitId,
        TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.replacement.successorUnitId')) successorUnitId,
        TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.replacement.successorUnitId')) correctionGroupId
      FROM @Original original CROSS APPLY OPENJSON(@Canonical,'$.sourceDecisions') decision
      WHERE original.Side='Recognition' AND JSON_VALUE(decision.value,'$.sourceKind')='PurchaseRecognition'
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=original.EventId
        AND JSON_QUERY(decision.value,'$.replacement') IS NOT NULL FOR JSON PATH);
    IF EXISTS(SELECT 1 FROM OPENJSON(@Bindings) binding
      WHERE EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.successorUnitId')))
        OR EXISTS(SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.correctionGroupId'))))
      OR EXISTS(SELECT JSON_VALUE(value,'$.successorUnitId') FROM OPENJSON(@Bindings) GROUP BY JSON_VALUE(value,'$.successorUnitId') HAVING COUNT(*)>1)
      THROW 51009,'Retained receipt successors require fresh whole-unit bindings.',1;
  END;
  SET @Units=(SELECT original.UnitId unitId,
      JSON_QUERY((SELECT EventId eventId,Side side,EventRevision eventRevision,JournalId journalId,PostingDate postingDate,EvidenceHash evidenceSha256
        FROM @Original sides WHERE sides.UnitId=original.UnitId ORDER BY Side FOR JSON PATH)) originals,
      JSON_QUERY((SELECT binding.value FROM OPENJSON(@Bindings) binding
        WHERE JSON_VALUE(binding.value,'$.originalUnitId')=CONVERT(nvarchar(36),original.UnitId))) binding
    FROM @Original original GROUP BY original.UnitId ORDER BY original.UnitId FOR JSON PATH);
  SET @Source=(SELECT 'SupplierBill' sourceKind,@Bill sourceId,@Revision sourceRevisionId,CONVERT(varchar(18),@Version,1) version,
    JSON_VALUE(@Payload,'$.total') amount,@Posting postingDate,@Review reviewId,
    CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Payload)),2) payloadSha256,
    @Successor replacementId,@SuccessorRevision replacementRevisionId,JSON_QUERY(@Replacement) replacement,
    JSON_VALUE(@SuccessorPayload,'$.total') replacementAmount,
    CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@SuccessorPayload)),2) successorPayloadSha256
    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
END;
