-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostSupplierRecognitionCreditAdjustment
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@Adjustment nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Recognition uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Adjustment,'$.recognitionEventId'));
  EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N'RecognitionAdjustment',@Recognition,@Adjustment,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  DECLARE @Amount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Adjustment,'$.amount')),
    @Date date=CONVERT(date,JSON_VALUE(@Adjustment,'$.postingDate'),23),@Id uniqueidentifier=NEWID(),@Request uniqueidentifier=NEWID(),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Currency varchar(3)=JSON_VALUE(@Plan,'$.command.currency'),@Journal uniqueidentifier,@Event uniqueidentifier,
    @Reason nvarchar(2000)=JSON_VALUE(@Plan,'$.command.reason');
  DECLARE @Snapshot nvarchar(max)=(SELECT @OwnerRequestId ownerRequestId,@Correction correctionId,@Group groupId,
      JSON_QUERY(@Adjustment) adjustment FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
    @Lines nvarchar(max)=(SELECT ordinal,accountId,accountVersion,
      CONVERT(nvarchar(60),CASE WHEN ordinal=1 AND @Amount>0 OR ordinal=2 AND @Amount<0 THEN ABS(@Amount) ELSE 0 END) debit,
      CONVERT(nvarchar(60),CASE WHEN ordinal=2 AND @Amount>0 OR ordinal=1 AND @Amount<0 THEN ABS(@Amount) ELSE 0 END) credit
      FROM(VALUES(1,CONVERT(uniqueidentifier,JSON_VALUE(@Adjustment,'$.expenseAccountId')),CONVERT(uniqueidentifier,JSON_VALUE(@Adjustment,'$.expenseAccountVersion'))),
        (2,CONVERT(uniqueidentifier,JSON_VALUE(@Adjustment,'$.prepaymentAccountId')),CONVERT(uniqueidentifier,JSON_VALUE(@Adjustment,'$.prepaymentAccountVersion'))))
      account(ordinal,accountId,accountVersion) ORDER BY ordinal FOR JSON PATH);
  DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
  INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierCreditsCorrect',N'Supplier.RecognitionCreditAdjustment',1,
    @Snapshot,N'SupplierRecognitionCreditAdjustment',@Id,@Correction,N'Adjust',1,@Config,@Currency,@Date,@Date,@Date,NULL,@Reason,@Snapshot,@Lines;
  SELECT @Journal=JournalId,@Event=SourceEventId FROM @Posted;
  UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Event;
  UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND RequestId=@Request;
  UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@Tenant AND TargetId=@Journal AND Action='Accounting.PostJournal';
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,RecognitionEventId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'RecognitionAdjustment',@Id,@Journal,@Recognition,@Adjustment);
END;
