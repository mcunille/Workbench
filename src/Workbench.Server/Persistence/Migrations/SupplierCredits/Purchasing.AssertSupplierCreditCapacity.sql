-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.AssertSupplierCreditCapacity
  @TenantId uniqueidentifier,@Claims nvarchar(max),@PostingDate date,@CorrectionGroupId uniqueidentifier=NULL,@ProposedCreditId uniqueidentifier=NULL
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Credit capacity requires its owner transaction.',1;
  -- Corrections must supply a complete authenticated proposal through their source owner.
  IF @CorrectionGroupId IS NOT NULL
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts receipt
      CROSS APPLY OPENJSON(receipt.ResultJson,'$.plan.sources') source
      JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=receipt.TenantId AND correction.GroupId=receipt.GroupId
        AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
        AND correction.ReplacementSourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId'))
      WHERE receipt.TenantId=@TenantId AND receipt.GroupId=@CorrectionGroupId AND receipt.Operation='CorrectSupplierCredit'
        AND correction.ReplacementSourceId=@ProposedCreditId
        AND JSON_VALUE(receipt.ResultJson,'$.state')='pending'
        AND CONVERT(varbinary(max),JSON_QUERY(source.value,'$.replacement.claims'))=CONVERT(varbinary(max),@Claims)
        AND TRY_CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23)=@PostingDate)
      THROW 51004,'Credit capacity requires its exact pending source proposal.',1;
    RETURN;
  END;
  DECLARE @Proposed TABLE(EventId uniqueidentifier,ComponentKey nvarchar(200),Amount decimal(28,4));
  INSERT @Proposed SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),
    JSON_VALUE(value,'$.componentKey'),TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Claims);
  IF EXISTS(SELECT 1 FROM @Proposed p WHERE p.Amount IS NULL OR p.Amount<=0 OR NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierCreditPosition(@TenantId,p.EventId,p.ComponentKey,@PostingDate)))
    THROW 51004,'Supported authentic credit component position required.',1;
  -- A late-recorded prepayment reduction cannot invalidate any already-posted consumption.
  IF EXISTS(SELECT 1 FROM @Proposed p CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,p.EventId,p.ComponentKey,@PostingDate) position
    JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=@TenantId AND matched.InvoiceEventId=p.EventId
    JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=matched.TenantId AND recognition.Id=matched.RecognitionEventId
    WHERE position.Treatment='Prepayment' AND recognition.PostingDate>@PostingDate)
    THROW 51009,'Credit invalidates a later prepayment recognition boundary.',1;
  IF @ProposedCreditId IS NULL OR @ProposedCreditId='00000000-0000-0000-0000-000000000000'
    THROW 51004,'A fresh proposed credit source is required.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND Id=@ProposedCreditId)
    THROW 51009,'The proposed credit source already exists.',1;
  DECLARE @Config uniqueidentifier=(SELECT Version FROM Accounting.Configurations WHERE TenantId=@TenantId),
    @Replacement nvarchar(max),@Sources nvarchar(max),@Positions nvarchar(max),@Adjustments nvarchar(max);
  SET @Replacement=(SELECT @Config expectedConfigurationVersion,@PostingDate postingDate,JSON_QUERY(@Claims) claims FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  SET @Sources=(SELECT @ProposedCreditId sourceId,@PostingDate postingDate,JSON_QUERY(@Replacement) replacement FOR JSON PATH);
  -- The same signed, all-date component/whole-unit stream governs normal postings and corrections.
  -- This is a fresh real source intent; it cannot subtract an existing source's claims.
  EXEC Purchasing.PlanSupplierCreditCapacity @TenantId,@Sources,@Positions OUTPUT,@Adjustments OUTPUT;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Adjustments))
    THROW 51009,'A normal credit cannot silently change an existing recognition dependency.',1;
END;
