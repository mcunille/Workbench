-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.AssertSupplierCreditCapacity
  @TenantId uniqueidentifier,@Claims nvarchar(max),@PostingDate date,@CorrectionGroupId uniqueidentifier=NULL
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Credit capacity requires its owner transaction.',1;
  -- Corrections must supply a complete authenticated proposal through their source owner.
  IF @CorrectionGroupId IS NOT NULL THROW 51004,'Credit correction capacity owner unavailable.',1;
  DECLARE @Proposed TABLE(EventId uniqueidentifier,ComponentKey nvarchar(200),Amount decimal(28,4));
  INSERT @Proposed SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),
    JSON_VALUE(value,'$.componentKey'),TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Claims);
  IF EXISTS(SELECT 1 FROM @Proposed p WHERE p.Amount IS NULL OR p.Amount<=0 OR NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierCreditPosition(@TenantId,p.EventId,p.ComponentKey,@PostingDate)))
    THROW 51004,'Supported authentic credit component position required.',1;
  IF EXISTS(SELECT 1 FROM @Proposed p CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,p.EventId,p.ComponentKey,@PostingDate) position
    WHERE COALESCE((SELECT SUM(CONVERT(decimal(38,4),c.Amount)) FROM Purchasing.SupplierCreditClaims c
      WHERE c.TenantId=@TenantId AND c.RecognitionEventId=p.EventId AND c.ComponentKey=p.ComponentKey),0)
      +(SELECT SUM(CONVERT(decimal(38,4),other.Amount)) FROM @Proposed other WHERE other.EventId=p.EventId AND other.ComponentKey=p.ComponentKey)>position.Capacity)
    THROW 51009,'Credit exceeds the surviving source component capacity.',1;
  -- Assigned rounding/discounts belong to the original whole cost, never a second allowance.
  IF EXISTS(SELECT 1 FROM @Proposed p WHERE
    COALESCE((SELECT SUM(CONVERT(decimal(38,4),c.Amount)) FROM Purchasing.SupplierCreditClaims c
      WHERE c.TenantId=@TenantId AND c.RecognitionEventId=p.EventId),0)
      +(SELECT SUM(CONVERT(decimal(38,4),other.Amount)) FROM @Proposed other WHERE other.EventId=p.EventId)
      >(SELECT SUM(CASE WHEN original.Kind='Discount' THEN -CONVERT(decimal(38,4),original.Amount)
        WHEN original.Kind='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),original.Amount) END)
        FROM Purchasing.RecognitionComponents original WHERE original.TenantId=@TenantId AND original.EventId=p.EventId))
    THROW 51009,'Credit exceeds the original attributed whole-unit cost.',1;
  -- A late-recorded prepayment reduction cannot invalidate any already-posted consumption.
  IF EXISTS(SELECT 1 FROM @Proposed p CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,p.EventId,p.ComponentKey,@PostingDate) position
    JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=@TenantId AND matched.InvoiceEventId=p.EventId
    JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=matched.TenantId AND recognition.Id=matched.RecognitionEventId
    WHERE position.Treatment='Prepayment' AND recognition.PostingDate>@PostingDate)
    THROW 51009,'Credit invalidates a later prepayment recognition boundary.',1;
END;
