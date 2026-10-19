-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RequireSupplierSourceComposition
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,
  @ChildKind nvarchar(40),@EntityId uniqueidentifier,@Details nvarchar(max),
  @GroupId uniqueidentifier OUTPUT,@CorrectionId uniqueidentifier OUTPUT,@RecordedAtUtc datetimeoffset OUTPUT,@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Canonical nvarchar(max),@Root uniqueidentifier;
  IF @@TRANCOUNT=0 OR @Tenant IS NULL OR @OwnerRequestId IS NULL OR @EntityId IS NULL
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@Tenant),'Transaction')<>'Exclusive'
    THROW 51009,'Source composition requires its owner transaction.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId AND Operation='CorrectSupplierBill')
  BEGIN
    EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@OwnerRequestId,@ChildKind,@EntityId,@Details,
      @GroupId OUTPUT,@CorrectionId OUTPUT,@RecordedAtUtc OUTPUT,@Plan OUTPUT;
    RETURN;
  END;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId
    AND Operation IN('CorrectSupplierRefund','CorrectSupplierRefundRepayment'))
  BEGIN
    EXEC Purchasing.RequireSupplierCashComposition @ActorId,@SessionId,@OwnerRequestId,@ChildKind,@EntityId,@Details,
      @GroupId OUTPUT,@CorrectionId OUTPUT,@RecordedAtUtc OUTPUT,@Plan OUTPUT;
    RETURN;
  END;
  BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsCorrect'; END TRY
  BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current source correction authority is required.',1; THROW; END CATCH;
  SET @GroupId=NULL; SET @CorrectionId=NULL; SET @RecordedAtUtc=NULL; SET @Plan=NULL;
  SELECT @GroupId=receipt.GroupId,@RecordedAtUtc=receipt.RecordedAtUtc,@Plan=JSON_QUERY(receipt.ResultJson,'$.plan'),
    @Canonical=receipt.CanonicalInput,@Root=financialGroup.SourceId
    FROM Purchasing.SupplierFinancialReceipts receipt
    JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=receipt.TenantId AND financialGroup.Id=receipt.GroupId
      AND financialGroup.Operation='CorrectCredit' AND financialGroup.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.sourceId'))
      AND financialGroup.RecordedAtUtc=receipt.RecordedAtUtc
    WHERE receipt.TenantId=@Tenant AND receipt.RequestId=@OwnerRequestId AND receipt.ActorId=@ActorId
      AND receipt.Operation='CorrectSupplierCredit' AND JSON_VALUE(receipt.ResultJson,'$.state')='pending';
  IF @GroupId IS NULL OR @Plan IS NULL OR @RecordedAtUtc IS NULL
    OR JSON_VALUE(@Canonical,'$.expectedPlanFingerprint')<>JSON_VALUE(@Plan,'$.planFingerprint')
    OR CONVERT(varbinary(max),JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL))<>CONVERT(varbinary(max),JSON_QUERY(@Plan,'$.command'))
    THROW 51004,'An authentic actor-bound pending source correction is required.',1;
  IF @ChildKind='ReplacementCredit'
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsPost'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current successor credit posting authority is required.',1; THROW; END CATCH;
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
      CROSS APPLY OPENJSON(@Plan,'$.sources') source
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND correction.SourceKind='SupplierCredit' AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
        AND correction.ReplacementSourceId=@EntityId AND correction.ReplacementSourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementRevisionId'))
        AND CONVERT(varbinary(max),@Details)=CONVERT(varbinary(max),JSON_QUERY(source.value,'$.replacement'));
  END
  ELSE IF @ChildKind='ReleaseSettlement'
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current settlement release authority is required.',1; THROW; END CATCH;
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
      CROSS APPLY OPENJSON(@Plan,'$.settlements') settlement
      JOIN Purchasing.SupplierCashSettlements actual ON actual.TenantId=correction.TenantId AND actual.Id=@EntityId
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND correction.SourceKind='SupplierCredit' AND correction.SourceId=actual.ItemId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.settlementId'))=actual.Id
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.fundingItemId'))=actual.ItemId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.cashSourceId'))=actual.CashSourceId
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(settlement.value,'$.amount'))=actual.Amount
        AND TRY_CONVERT(date,JSON_VALUE(settlement.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23);
  END
  ELSE IF @ChildKind='ReverseSource'
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
      CROSS APPLY OPENJSON(@Plan,'$.sources') source
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND correction.SourceKind='SupplierCredit' AND correction.SourceId=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))=correction.SourceId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceRevisionId'))=correction.SourceRevisionId
        AND TRY_CONVERT(date,JSON_VALUE(source.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23)
        AND correction.OriginalSourceVersion=TRY_CONVERT(binary(8),JSON_VALUE(source.value,'$.version'),1);
  ELSE IF @ChildKind IN('Application','ReverseApplication')
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
    IF @ChildKind='Application' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reapplications') application
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.fundingItemId'))=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.debtItemId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.debtItemId'))
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(application.value,'$.amount'))=TRY_CONVERT(decimal(28,4),JSON_VALUE(@Details,'$.amount'))
        AND TRY_CONVERT(date,JSON_VALUE(application.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SELECT TOP(1) @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
        WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
          AND correction.SourceKind='SupplierCredit' AND (correction.ReplacementSourceId=@EntityId OR correction.SourceId=@Root)
        ORDER BY CASE WHEN correction.ReplacementSourceId=@EntityId THEN 0 ELSE 1 END,correction.Id;
    IF @ChildKind='ReverseApplication'
      SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
        CROSS APPLY OPENJSON(@Plan,'$.applications') application
        WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
          AND correction.SourceKind='SupplierCredit' AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.fundingItemId'))
          AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.applicationId'))=@EntityId
          AND TRY_CONVERT(date,JSON_VALUE(application.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23);
  END;
  ELSE IF @ChildKind='RecognitionAdjustment'
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
      CROSS APPLY OPENJSON(@Plan,'$.recognitionAdjustments') adjustment
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND correction.SourceKind='SupplierCredit' AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.sourceId'))
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.sourceId'))=correction.SourceId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.recognitionEventId'))=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.invoiceEventId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.invoiceEventId'))
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(adjustment.value,'$.amount'))=TRY_CONVERT(decimal(28,4),JSON_VALUE(@Details,'$.amount'))
        AND CONVERT(varbinary(max),adjustment.value)=CONVERT(varbinary(max),@Details);
  IF @CorrectionId IS NULL THROW 51004,'The exact source-owned child composition is unavailable.',1;
END;
