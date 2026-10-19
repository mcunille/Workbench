-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.AppendSupplierCashSettlement
  @TenantId uniqueidentifier,@CashSourceId uniqueidentifier,@JournalId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Cash settlement append requires its owner transaction.',1;
  DECLARE @Group uniqueidentifier,@Event uniqueidentifier,@Date date,@Now datetimeoffset,@Amount decimal(28,4),
    @Kind varchar(32),@Snapshot nvarchar(max),@Canonical nvarchar(max),@Po uniqueidentifier,@Supplier uniqueidentifier,@Currency varchar(3);
  SELECT @Kind=c.Kind,@Group=c.GroupId,@Event=sourceEvent.Id,@Date=c.PostingDate,@Now=c.RecordedAtUtc,@Amount=c.Amount,
    @Snapshot=c.SourceSnapshotJson,@Canonical=receipt.CanonicalInput,@Po=c.PurchaseOrderId,@Supplier=c.SupplierId,@Currency=c.Currency
    FROM Purchasing.SupplierCashSources c
    JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=c.TenantId AND financialGroup.Id=c.GroupId
      AND ((financialGroup.SourceId=c.Id AND financialGroup.Operation=CASE c.Kind WHEN 'SupplierRefund' THEN 'RecordRefund' ELSE 'RecordRepayment' END)
        OR EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections parent WHERE parent.TenantId=c.TenantId AND parent.GroupId=c.GroupId
          AND parent.SourceKind=c.Kind AND parent.OriginalCashSourceId=parent.SourceId
          AND parent.ReplacementSourceId=c.Id AND parent.ReplacementSourceRevisionId=c.RevisionId
          AND parent.ActorId=c.ActorId AND parent.RecordedAtUtc=c.RecordedAtUtc
          AND parent.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.SourceSnapshotJson,'$.correctionId'))
          AND financialGroup.Operation IN('CorrectRefund','CorrectRepayment'))) AND financialGroup.RecordedAtUtc=c.RecordedAtUtc
    JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.GroupId=c.GroupId
      AND receipt.Operation=CASE c.Kind WHEN 'SupplierRefund' THEN 'RecordSupplierRefund' ELSE 'RecordSupplierRefundRepayment' END AND receipt.ActorId=c.ActorId AND receipt.RecordedAtUtc=c.RecordedAtUtc
      AND JSON_VALUE(receipt.ResultJson,'$.state')='pending'
      AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(c.SourceSnapshotJson,'$.command'))
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=c.TenantId AND sourceEvent.SourceKind=c.Kind AND sourceEvent.SourceId=c.Id
      AND sourceEvent.SourceRevision=c.RevisionId AND sourceEvent.EventKind=CASE c.Kind WHEN 'SupplierRefund' THEN 'Refund' ELSE 'RefundRepayment' END AND sourceEvent.ActorId=c.ActorId
      AND sourceEvent.PostingDate=c.PostingDate AND sourceEvent.DocumentDate=c.ActualDate AND sourceEvent.EffectiveDate=c.EffectiveDate
      AND sourceEvent.RecordedAtUtc=c.RecordedAtUtc AND CONVERT(varbinary(max),sourceEvent.SnapshotJson)=CONVERT(varbinary(max),c.SourceSnapshotJson)
    JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id AND journal.Id=@JournalId
      AND journal.Currency=c.Currency AND journal.PostingDate=c.PostingDate AND journal.RecordedAtUtc=c.RecordedAtUtc
    JOIN Accounting.PostingReceipts posted ON posted.TenantId=journal.TenantId AND posted.SourceEventId=sourceEvent.Id AND posted.JournalId=journal.Id
      AND posted.SourceCommandKind=CASE c.Kind WHEN 'SupplierRefund' THEN 'Supplier.Refund' ELSE 'Supplier.RefundRepayment' END AND posted.ActorId=c.ActorId AND posted.RecordedAtUtc=c.RecordedAtUtc
      AND CONVERT(varbinary(max),posted.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput)
    JOIN Accounting.JournalLines funding ON funding.TenantId=journal.TenantId AND funding.JournalId=journal.Id AND funding.Ordinal=1
      AND funding.AccountId=c.FundingAccountId AND funding.AccountVersion=c.FundingAccountVersion AND funding.AccountPurpose=c.FundingAccountPurpose
      AND funding.AccountType='Asset' AND funding.AccountPurpose IN('Bank','Cash') AND funding.Debit=CASE c.Kind WHEN 'SupplierRefund' THEN c.Amount ELSE 0 END
      AND funding.Credit=CASE c.Kind WHEN 'SupplierRefundRepayment' THEN c.Amount ELSE 0 END
    WHERE c.TenantId=@TenantId AND c.Id=@CashSourceId AND c.Kind IN('SupplierRefund','SupplierRefundRepayment');
  IF @Group IS NULL THROW 51004,'Authentic pending refund source required.',1;
  DECLARE @Targets TABLE(Id uniqueidentifier PRIMARY KEY,MovementId uniqueidentifier,ItemId uniqueidentifier UNIQUE,
    Amount decimal(28,4),AccountId uniqueidentifier,AccountVersion uniqueidentifier,JournalOrdinal int);
  INSERT @Targets SELECT Id,MovementId,ItemId,Amount,AccountId,AccountVersion,JournalOrdinal
    FROM OPENJSON(@Snapshot,'$.settlements') WITH(Id uniqueidentifier '$.settlementId',MovementId uniqueidentifier '$.movementId',
      ItemId uniqueidentifier '$.itemId',Amount decimal(28,4) '$.amount',AccountId uniqueidentifier '$.accountId',
      AccountVersion uniqueidentifier '$.accountVersion',JournalOrdinal int '$.journalOrdinal');
  IF (SELECT COUNT(*) FROM @Targets)<>(SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.settlements'))
    OR (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets)<>@Amount
    OR EXISTS(SELECT ItemId,Amount FROM @Targets EXCEPT SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),
      CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Canonical,'$.settlements'))
    OR (SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND CASE @Kind WHEN 'SupplierRefund' THEN Debit ELSE Credit END>0)<>1
    OR EXISTS(SELECT JournalOrdinal,AccountId,AccountVersion,SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets GROUP BY JournalOrdinal,AccountId,AccountVersion
      EXCEPT SELECT Ordinal,AccountId,AccountVersion,CASE @Kind WHEN 'SupplierRefund' THEN Credit ELSE Debit END FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND CASE @Kind WHEN 'SupplierRefund' THEN Credit ELSE Debit END>0)
    OR EXISTS(SELECT Ordinal,AccountId,AccountVersion,CASE @Kind WHEN 'SupplierRefund' THEN Credit ELSE Debit END FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND CASE @Kind WHEN 'SupplierRefund' THEN Credit ELSE Debit END>0
      EXCEPT SELECT JournalOrdinal,AccountId,AccountVersion,SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets GROUP BY JournalOrdinal,AccountId,AccountVersion)
    OR EXISTS(SELECT 1 FROM @Targets target WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item
      CROSS APPLY Purchasing.SupplierCashTargetControl(@TenantId,@Kind,item.Id) control
      JOIN Accounting.JournalLines line ON line.TenantId=item.TenantId AND line.JournalId=@JournalId AND line.Ordinal=target.JournalOrdinal
        AND line.AccountId=control.AccountId AND line.AccountVersion=control.AccountVersion AND line.AccountCode=control.AccountCode
        AND line.AccountName=control.AccountName AND line.AccountType=control.AccountType AND line.AccountPurpose=control.AccountPurpose
      WHERE item.TenantId=@TenantId AND item.Id=target.ItemId AND ((@Kind='SupplierRefund' AND item.Kind IN('Advance','CreditReceivable')) OR (@Kind='SupplierRefundRepayment' AND item.Kind='RefundClearing')) AND item.SupplierId=@Supplier
        AND item.PurchaseOrderId=@Po AND item.Currency=@Currency AND item.SourcePostingDate<=@Date
        AND target.AccountId=control.AccountId AND target.AccountVersion=control.AccountVersion))
    THROW 51004,'Complete historical refund settlement attribution required.',1;
  DECLARE @Movements nvarchar(max)=(SELECT ItemId itemId,@Group groupId,@Date postingDate,CONVERT(nvarchar(60),-Amount) amount FROM @Targets FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    SELECT @TenantId,MovementId,ItemId,@Group,'CashSettlement',@Event,@Date,-Amount,@Now FROM @Targets;
  INSERT Purchasing.SupplierCashSettlements(TenantId,Id,CashSourceId,ItemId,Amount,MovementId,JournalId,JournalOrdinal,AccountId,AccountVersion)
    SELECT @TenantId,Id,@CashSourceId,ItemId,Amount,MovementId,@JournalId,JournalOrdinal,AccountId,AccountVersion FROM @Targets;
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @TenantId,NEWID(),@Group,target.MovementId,@JournalId,target.JournalOrdinal,target.AccountId,target.AccountVersion,line.AccountPurpose,-target.Amount
    FROM @Targets target JOIN Accounting.JournalLines line ON line.TenantId=@TenantId AND line.JournalId=@JournalId AND line.Ordinal=target.JournalOrdinal;
  UPDATE version SET ItemId=version.ItemId FROM Purchasing.SupplierItemVersions version JOIN @Targets target ON target.ItemId=version.ItemId WHERE version.TenantId=@TenantId;
  INSERT Purchasing.SupplierCashVersions(TenantId,CashSourceId) VALUES(@TenantId,@CashSourceId);
END;
