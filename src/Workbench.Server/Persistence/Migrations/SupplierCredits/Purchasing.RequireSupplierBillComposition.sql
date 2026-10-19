-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RequireSupplierBillComposition
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,
  @ChildKind nvarchar(40),@EntityId uniqueidentifier,@Details nvarchar(max),
  @GroupId uniqueidentifier OUTPUT,@CorrectionId uniqueidentifier OUTPUT,@RecordedAtUtc datetimeoffset OUTPUT,@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Canonical nvarchar(max),@Root uniqueidentifier;
  IF @@TRANCOUNT=0 OR @Tenant IS NULL OR @OwnerRequestId IS NULL OR @EntityId IS NULL
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@Tenant),'Transaction')<>'Exclusive'
    THROW 51009,'Bill composition requires its owner transaction.',1;
  BEGIN TRY
    EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsCorrect';
  END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current bill correction authority is required.',1; THROW; END CATCH;
  SET @GroupId=NULL; SET @CorrectionId=NULL; SET @RecordedAtUtc=NULL; SET @Plan=NULL;
  SELECT @GroupId=receipt.GroupId,@RecordedAtUtc=receipt.RecordedAtUtc,@Plan=JSON_QUERY(receipt.ResultJson,'$.plan'),
      @Canonical=receipt.CanonicalInput,@Root=financialGroup.SourceId
    FROM Purchasing.SupplierFinancialReceipts receipt
    JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=receipt.TenantId AND financialGroup.Id=receipt.GroupId
      AND financialGroup.Operation='CorrectBill' AND financialGroup.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.sourceId'))
      AND financialGroup.RecordedAtUtc=receipt.RecordedAtUtc
    WHERE receipt.TenantId=@Tenant AND receipt.RequestId=@OwnerRequestId AND receipt.ActorId=@ActorId
      AND receipt.Operation='CorrectSupplierBill' AND JSON_VALUE(receipt.ResultJson,'$.state')='pending';
  IF @GroupId IS NULL OR @Plan IS NULL OR @RecordedAtUtc IS NULL
    OR JSON_VALUE(@Canonical,'$.expectedPlanFingerprint')<>JSON_VALUE(@Plan,'$.planFingerprint')
    OR CONVERT(varbinary(max),JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL))<>CONVERT(varbinary(max),JSON_QUERY(@Plan,'$.command'))
    THROW 51004,'An authentic actor-bound pending bill correction is required.',1;
  DECLARE @RootCorrection uniqueidentifier;
  SELECT @RootCorrection=correction.Id FROM Purchasing.SupplierSourceCorrections correction
    WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
      AND correction.SourceKind='SupplierBill' AND correction.SourceId=@Root
      AND correction.SourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].sourceRevisionId'))
      AND correction.OriginalSourceVersion=TRY_CONVERT(binary(8),JSON_VALUE(@Plan,'$.sources[0].version'),1);
  IF @RootCorrection IS NULL THROW 51004,'The pending bill source membership is unavailable.',1;
  IF JSON_QUERY(@Canonical,'$.replacement') IS NOT NULL
  BEGIN
    BEGIN TRY
      EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsManage';
      EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsPost';
    END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current successor bill review/posting authority is required.',1; THROW; END CATCH;
  END;
  IF @ChildKind='ReverseBill' AND @EntityId=@Root
    AND TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Plan,'$.sources[0].postingDate'),23)
    SET @CorrectionId=@RootCorrection;
  ELSE IF @ChildKind='RecognitionSuccessors' AND @EntityId=@Root
    SET @CorrectionId=@RootCorrection;
  ELSE IF @ChildKind='ReplacementBill'
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsPost'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current successor bill posting authority is required.',1; THROW; END CATCH;
    IF @EntityId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacementId'))
      AND CONVERT(varbinary(max),@Details)=CONVERT(varbinary(max),JSON_QUERY(@Plan,'$.sources[0].replacement.post'))
      SET @CorrectionId=@RootCorrection;
  END
  ELSE IF @ChildKind='SuccessorRecognition'
  BEGIN
    IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.units') unit
      JOIN Purchasing.RecognitionCorrectionGroups correction ON correction.TenantId=@Tenant
        AND correction.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.correctionGroupId'))
        AND correction.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.unitId'))
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc AND correction.Operation='Replace'
        AND correction.ReplacementUnitId IS NULL
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId'))=@EntityId
        AND ((JSON_VALUE(@Details,'$.sides[0].side')='Invoice'
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.sides[0].sourceId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacementId'))
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.sides[0].sourceRevision'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacementRevisionId')))
        OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.recognitionSuccessors') successor
          WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(successor.value,'$.unitId'))=@EntityId
            AND CONVERT(varbinary(max),JSON_QUERY(successor.value,'$.command.units[0]'))=CONVERT(varbinary(max),@Details))))
      SET @CorrectionId=@RootCorrection;
  END
  ELSE IF @ChildKind IN('Application','ReverseApplication')
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
    IF @ChildKind='Application' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reapplications') application
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.fundingItemId'))=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.debtItemId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.debtItemId'))
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(application.value,'$.amount'))=TRY_CONVERT(decimal(28,4),JSON_VALUE(@Details,'$.amount'))
        AND TRY_CONVERT(date,JSON_VALUE(application.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
    IF @ChildKind='ReverseApplication' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.applications') application
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(application.value,'$.applicationId'))=@EntityId
        AND TRY_CONVERT(date,JSON_VALUE(application.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
  END;
  ELSE IF @ChildKind IN('ReverseSource','ReleaseSettlement','RecognitionAdjustment','ReverseRecognitionAdjustment','ReplacementCredit')
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsCorrect'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current dependent credit correction authority is required.',1; THROW; END CATCH;
    IF @ChildKind='ReplacementCredit'
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsPost'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current successor credit posting authority is required.',1; THROW; END CATCH;
      SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction CROSS APPLY OPENJSON(@Plan,'$.sources') source
        WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceKind='SupplierCredit'
          AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
          AND correction.ReplacementSourceId=@EntityId AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
          AND correction.ReplacementSourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementRevisionId'))
          AND CONVERT(varbinary(max),@Details)=CONVERT(varbinary(max),JSON_QUERY(source.value,'$.replacement'));
    END;
    IF @ChildKind='ReverseRecognitionAdjustment' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reverseAdjustments') adjustment
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.journalId'))=@EntityId
        AND TRY_CONVERT(date,JSON_VALUE(adjustment.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
    IF @ChildKind='ReverseSource'
      SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction CROSS APPLY OPENJSON(@Plan,'$.sources') source
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceKind='SupplierCredit' AND correction.SourceId=@EntityId
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))=correction.SourceId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceRevisionId'))=correction.SourceRevisionId
        AND correction.OriginalSourceVersion=TRY_CONVERT(binary(8),JSON_VALUE(source.value,'$.version'),1)
        AND TRY_CONVERT(date,JSON_VALUE(source.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23);
    IF @ChildKind='ReleaseSettlement'
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current settlement release authority is required.',1; THROW; END CATCH;
      SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
        CROSS APPLY OPENJSON(@Plan,'$.settlements') settlement
        JOIN Purchasing.SupplierCashSettlements actual ON actual.TenantId=correction.TenantId AND actual.Id=@EntityId
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceKind='SupplierCredit' AND correction.SourceId=actual.ItemId
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.settlementId'))=actual.Id
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.fundingItemId'))=actual.ItemId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(settlement.value,'$.cashSourceId'))=actual.CashSourceId
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(settlement.value,'$.amount'))=actual.Amount
        AND TRY_CONVERT(date,JSON_VALUE(settlement.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23);
    END;
    IF @ChildKind='RecognitionAdjustment'
      SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction
        CROSS APPLY OPENJSON(@Plan,'$.recognitionAdjustments') adjustment
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceKind='SupplierCredit'
        AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.sourceId'))
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.recognitionEventId'))=@EntityId
        AND CONVERT(varbinary(max),adjustment.value)=CONVERT(varbinary(max),@Details);
  END;
  IF @CorrectionId IS NULL THROW 51004,'The exact source-owned bill child composition is unavailable.',1;
END;
