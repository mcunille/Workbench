-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ApplySupplierClearingFunds
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@PostingDate date,
  @FundingItemId uniqueidentifier,@DebtItemId uniqueidentifier,@Amount decimal(28,4),@Position int
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,
    @Correction uniqueidentifier,@Now datetimeoffset,@Plan nvarchar(max),@Details nvarchar(max)=(SELECT
      @DebtItemId debtItemId,CONVERT(nvarchar(60),@Amount) amount,@PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N'Application',@FundingItemId,@Details,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  IF @Amount IS NULL OR @Amount<=0 OR @FundingItemId=@DebtItemId
    THROW 51000,'Invalid source-owned application.',1;
  DECLARE @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Plan,'$.command.currency'),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Application uniqueidentifier=NEWID(),@Request uniqueidentifier=NEWID(),@FundingMovement uniqueidentifier=NEWID(),
    @DebtMovement uniqueidentifier=NEWID(),@Journal uniqueidentifier,@Event uniqueidentifier;
  IF (SELECT COUNT(*) FROM Purchasing.SupplierOpenItems item WITH(UPDLOCK,HOLDLOCK)
      JOIN Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=item.TenantId AND version.ItemId=item.Id
      CROSS APPLY Purchasing.SupplierItemControl(@Tenant,item.Id) control
      WHERE item.TenantId=@Tenant AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency
        AND item.SourcePostingDate<=@PostingDate AND
        (item.Id=@FundingItemId AND item.Kind IN('Advance','CreditReceivable') AND control.AccountType='Asset'
          OR item.Id=@DebtItemId AND item.Kind IN('Payable','RefundClearing') AND control.AccountType='Liability'))<>2
    THROW 51004,'Authentic current source-scoped application controls required.',1;
  DECLARE @Movements nvarchar(max)=(SELECT itemId,@Group groupId,@PostingDate postingDate,CONVERT(nvarchar(60),-@Amount) amount
    FROM(VALUES(@FundingItemId),(@DebtItemId)) item(itemId) FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @Tenant,@Movements;
  DECLARE @Snapshot nvarchar(max)=(SELECT @Application applicationId,@Group groupId,@FundingItemId fundingItemId,@DebtItemId debtItemId,
    @OwnerRequestId ownerRequestId,@Correction correctionId,CONVERT(nvarchar(60),@Amount) amount,@PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
    @Controls nvarchar(max)=(SELECT ordinal,itemId FROM(VALUES(1,@DebtItemId),(2,@FundingItemId)) item(ordinal,itemId) FOR JSON PATH),
    @Lines nvarchar(max)=(SELECT line.Ordinal ordinal,line.AccountId accountId,line.AccountVersion accountVersion,
      CONVERT(nvarchar(60),line.Debit) debit,CONVERT(nvarchar(60),line.Credit) credit
      FROM Purchasing.SupplierItemControl(@Tenant,@DebtItemId) debt
      CROSS JOIN Purchasing.SupplierItemControl(@Tenant,@FundingItemId) funding
      CROSS APPLY Accounting.ControlJournalPair(debt.AccountId,debt.AccountVersion,funding.AccountId,funding.AccountVersion,@Amount) line
      ORDER BY line.Ordinal FOR JSON PATH);
  DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
  INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Supplier.Apply',1,
    @Snapshot,N'SupplierApplication',@Application,@Group,N'Apply',1,@Config,@Currency,@PostingDate,@PostingDate,@PostingDate,
    NULL,NULL,@Snapshot,@Lines,@Controls;
  SELECT @Event=SourceEventId,@Journal=JournalId FROM @Posted;
  UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Event;
  UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND RequestId=@Request;
  UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@Tenant AND TargetId=@Journal AND Action='Accounting.PostJournal';
  INSERT Purchasing.SupplierApplications(TenantId,Id,GroupId,FundingItemId,DebtItemId,Amount,PostingDate,ActorId,RecordedAtUtc)
    VALUES(@Tenant,@Application,@Group,@FundingItemId,@DebtItemId,@Amount,@PostingDate,@ActorId,@Now);
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    VALUES(@Tenant,@FundingMovement,@FundingItemId,@Group,'Apply',@Event,@PostingDate,-@Amount,@Now),
      (@Tenant,@DebtMovement,@DebtItemId,@Group,'Apply',@Event,@PostingDate,-@Amount,@Now);
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @Tenant,NEWID(),@Group,CASE Ordinal WHEN 1 THEN @DebtMovement ELSE @FundingMovement END,@Journal,Ordinal,
      AccountId,AccountVersion,AccountPurpose,-@Amount FROM Accounting.JournalLines WHERE TenantId=@Tenant AND JournalId=@Journal;
  INSERT Purchasing.SupplierApplicationVersions(TenantId,ApplicationId) VALUES(@Tenant,@Application);
  UPDATE Purchasing.SupplierItemVersions SET ItemId=ItemId WHERE TenantId=@Tenant AND ItemId IN(@FundingItemId,@DebtItemId);
  IF (SELECT COUNT(*) FROM Purchasing.SupplierApplicationControl(@Tenant,@Application))<>1
    THROW 51004,'The source-owned application has incomplete historical evidence.',1;
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,ItemId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'Application',@Application,@Journal,@DebtItemId,JSON_MODIFY(@Snapshot,'$.position',@Position));
END;
