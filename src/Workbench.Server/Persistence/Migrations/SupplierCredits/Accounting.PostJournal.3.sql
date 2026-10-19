-- Copyright (c) 2026 The White Stag Collection.
  IF @SourceCommandKind=N'Supplier.Refund'
  BEGIN
    IF @SourceKind<>N'SupplierRefund' OR @EventKind<>N'Refund' OR @RequiredPermission<>N'SupplierRefundsRecord'
      OR @SupplierControlItems IS NOT NULL
      OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
      OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSources cash
        JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=cash.TenantId AND financialGroup.Id=cash.GroupId
          AND financialGroup.Operation='RecordRefund' AND financialGroup.SourceId=cash.Id AND financialGroup.RecordedAtUtc=cash.RecordedAtUtc
        JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=cash.TenantId AND receipt.GroupId=cash.GroupId
          AND receipt.RequestId=@RequestId AND receipt.Operation='RecordSupplierRefund' AND receipt.ActorId=cash.ActorId
          AND receipt.RecordedAtUtc=cash.RecordedAtUtc AND JSON_VALUE(receipt.ResultJson,'$.state')='pending'
          AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput)
        WHERE cash.TenantId=@TenantId AND cash.Id=@SourceId AND cash.RevisionId=@SourceRevision AND cash.Kind='SupplierRefund'
          AND cash.ActorId=@ActorId AND cash.Currency=@Currency AND cash.ActualDate=@DocumentDate
          AND cash.EffectiveDate=@EffectiveDate AND cash.PostingDate=@PostingDate
          AND CONVERT(varbinary(max),cash.SourceSnapshotJson)=CONVERT(varbinary(max),@SourceSnapshot)
          AND CONVERT(varbinary(max),JSON_QUERY(cash.SourceSnapshotJson,'$.command'))=CONVERT(varbinary(max),@CanonicalInput)
          AND @ExpectedConfigurationVersion=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@CanonicalInput,'$.expectedConfigurationVersion'))
          AND EXISTS(SELECT 1 FROM @Input input WHERE input.Ordinal=1 AND input.AccountId=cash.FundingAccountId
            AND input.AccountVersion=cash.FundingAccountVersion AND input.Debit=cash.Amount AND input.Credit=0))
      THROW 51004,'Authentic pending supplier refund owner required.',1;
    DECLARE @CashProof TABLE(ItemId uniqueidentifier,Amount decimal(28,4),AccountId uniqueidentifier,AccountVersion uniqueidentifier,
      AccountCode nvarchar(32),AccountName nvarchar(160),AccountType nvarchar(40),AccountPurpose nvarchar(40));
    INSERT @CashProof
      SELECT item.Id,CONVERT(decimal(28,4),JSON_VALUE(target.value,'$.amount')),control.AccountId,control.AccountVersion,
        control.AccountCode,control.AccountName,control.AccountType,control.AccountPurpose
      FROM OPENJSON(@CanonicalInput,'$.settlements') target
      JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=@TenantId AND cash.Id=@SourceId
      JOIN Purchasing.SupplierOpenItems item ON item.TenantId=cash.TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND item.Kind IN('Advance','CreditReceivable') AND item.SupplierId=cash.SupplierId AND item.PurchaseOrderId=cash.PurchaseOrderId
        AND item.Currency=cash.Currency AND item.SourcePostingDate<=cash.PostingDate
      CROSS APPLY Purchasing.SupplierAssetControl(@TenantId,item.Id) control
      WHERE control.AccountType='Asset' AND control.AccountPurpose IN('SupplierAdvance','SupplierCreditReceivable');
    IF (SELECT COUNT(*) FROM @CashProof)<>(SELECT COUNT(*) FROM OPENJSON(@CanonicalInput,'$.settlements'))
      OR (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @CashProof)<>(SELECT Debit FROM @Input WHERE Ordinal=1)
      THROW 51004,'Complete authentic refundable source controls required.',1;
    INSERT @Historical
      SELECT DISTINCT input.Ordinal,proof.AccountId,proof.AccountVersion,proof.AccountCode,proof.AccountName,proof.AccountType,proof.AccountPurpose
      FROM @Input input JOIN @CashProof proof ON proof.AccountId=input.AccountId AND proof.AccountVersion=input.AccountVersion
      WHERE input.Debit=0 AND input.Credit=(SELECT SUM(CONVERT(decimal(38,4),other.Amount)) FROM @CashProof other
        WHERE other.AccountId=proof.AccountId AND other.AccountVersion=proof.AccountVersion);
    IF (SELECT COUNT(*) FROM @Historical)<>(SELECT COUNT(*) FROM @Input WHERE Credit>0)
      OR (SELECT COUNT(*) FROM @Input WHERE Debit>0)<>1
      THROW 51004,'Complete historical refund control attribution required.',1;
  END;
  IF @SourceCommandKind=N'Supplier.Payment' AND @RequiredPermission=N'SupplierPaymentsCorrect'
  BEGIN
    DECLARE @CompositionOwner uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.correctionRequestId')),
      @CompositionGroup uniqueidentifier,@CompositionInstant datetimeoffset,@CompositionRequest uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.correctionCommandRequestId'));
    IF @CompositionOwner IS NULL THROW 51004,'Payment replacement requires its pending correction owner.',1;
    EXEC Purchasing.RequireSupplierComposition @ActorId,@CompositionOwner,@CompositionRequest,N'RecordSupplierPayment',@CanonicalInput,
      @CompositionGroup OUTPUT,@CompositionInstant OUTPUT;
    IF @CompositionGroup<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.groupId'))
      THROW 51004,'Payment replacement belongs to a different correction.',1;
  END;
  IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind=N'Supplier.Payment'
  BEGIN
    IF @SourceKind<>N'SupplierPayment' OR @EventKind<>N'Payment' OR @RequiredPermission NOT IN(N'SupplierPaymentsRecord',N'SupplierPaymentsCorrect')
      OR ISJSON(@SupplierControlItems,ARRAY)<>1 OR DATALENGTH(@SupplierControlItems)>262144
      OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
      THROW 51004,'Invalid payment historical control authority.',1;
    EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;
    EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
    IF EXISTS(SELECT 1 FROM OPENJSON(@SupplierControlItems) e WHERE e.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('ordinal','itemId')))
      OR EXISTS(SELECT JSON_VALUE(value,'$.itemId') FROM OPENJSON(@SupplierControlItems) GROUP BY JSON_VALUE(value,'$.itemId') HAVING COUNT(*)>1)
      OR (SELECT COUNT(*) FROM OPENJSON(@SupplierControlItems))<>(SELECT COUNT(*) FROM OPENJSON(@SourceSnapshot,'$.allocations'))
      THROW 51004,'Invalid payment control references.',1;
    DECLARE @PaymentProof TABLE(Ordinal int,ItemId uniqueidentifier,Amount decimal(28,4),AccountId uniqueidentifier,AccountVersion uniqueidentifier,
      AccountCode nvarchar(32),AccountName nvarchar(160),AccountType nvarchar(40),AccountPurpose nvarchar(40));
    INSERT @PaymentProof SELECT line.Ordinal,item.Id,TRY_CONVERT(decimal(28,4),JSON_VALUE(a.value,'$.amount')),
      h.AccountId,h.AccountVersion,h.AccountCode,h.AccountName,h.AccountType,h.AccountPurpose
      FROM OPENJSON(@SupplierControlItems) e
      JOIN OPENJSON(@SourceSnapshot,'$.allocations') a ON JSON_VALUE(a.value,'$.itemId')=JSON_VALUE(e.value,'$.itemId')
        AND JSON_VALUE(a.value,'$.ordinal')=JSON_VALUE(e.value,'$.ordinal')
      JOIN Purchasing.SupplierOpenItems item ON item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId'))
      JOIN Purchasing.SupplierBillPostings b ON b.TenantId=item.TenantId AND b.BillId=item.BillId AND b.RevisionId=item.SourceRevisionId
      CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) h
      JOIN @Input line ON line.Ordinal=TRY_CONVERT(int,JSON_VALUE(e.value,'$.ordinal')) AND line.AccountId=h.AccountId AND line.AccountVersion=h.AccountVersion
      WHERE item.Kind='Payable' AND item.SourceKind='SupplierBill' AND item.SourceId=item.BillId
        AND item.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.billId')) AND item.Currency=@Currency AND item.SourcePostingDate<=@PostingDate
        AND item.SupplierId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.command.supplierId'))
        AND item.PurchaseOrderId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.command.purchaseOrderId'))
        AND line.Debit>0 AND line.Credit=0 AND h.AccountPurpose='SupplierPayable' AND h.AccountType='Liability';
    IF (SELECT COUNT(*) FROM @PaymentProof)<>(SELECT COUNT(*) FROM OPENJSON(@SupplierControlItems))
      OR EXISTS(SELECT 1 FROM @PaymentProof WHERE Amount IS NULL OR Amount<=0)
      OR EXISTS(SELECT 1 FROM @Input l JOIN @PaymentProof p ON p.Ordinal=l.Ordinal GROUP BY l.Ordinal,l.Debit HAVING SUM(CONVERT(decimal(38,4),p.Amount))<>l.Debit)
      THROW 51004,'Payment historical control evidence is unavailable.',1;
    INSERT @Historical SELECT DISTINCT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose FROM @PaymentProof;
  END;

  IF @SourceCommandKind=N'Supplier.ReverseApplication'
  BEGIN
    IF @SourceKind<>N'SupplierApplicationReversal' OR @EventKind<>N'Reverse' OR @RequiredPermission<>N'SupplierAllocationsManage'
      OR @SupplierControlItems IS NULL
      THROW 51004,'Invalid supplier application inverse authority.',1;
    EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
    DECLARE @InverseApplication uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.applicationId'));
    IF (SELECT COUNT(*) FROM Purchasing.SupplierApplicationControl(@TenantId,@InverseApplication))<>1
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals WHERE TenantId=@TenantId AND ApplicationId=@InverseApplication)
      OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g
        ON g.TenantId=r.TenantId AND g.Id=r.GroupId WHERE r.TenantId=@TenantId AND r.GroupId=@SourceRevision AND r.ActorId=@ActorId
        AND JSON_VALUE(r.ResultJson,'$.state')='pending' AND (g.RecordedAtUtc=TRY_CONVERT(datetimeoffset,JSON_VALUE(r.ResultJson,'$.recordedAtUtc')) OR r.Operation IN('CorrectSupplierCredit','CorrectSupplierBill') AND g.RecordedAtUtc=r.RecordedAtUtc)
        AND ((r.Operation='ReverseSupplierApplication' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.applicationId'))=@InverseApplication)
          OR (r.Operation='CorrectSupplierPayment' AND EXISTS(SELECT 1 FROM Purchasing.SupplierApplications a WHERE a.TenantId=@TenantId AND a.Id=@InverseApplication
            AND a.FundingItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.paymentId'))))
          OR ((r.Operation='CorrectSupplierCredit' AND g.Operation='CorrectCredit' OR r.Operation='CorrectSupplierBill' AND g.Operation='CorrectBill') AND EXISTS(
            SELECT 1 FROM OPENJSON(r.ResultJson,'$.plan.applications') child
            WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(child.value,'$.applicationId'))=@InverseApplication
              AND TRY_CONVERT(date,JSON_VALUE(child.value,'$.postingDate'),23)=@PostingDate))))
      THROW 51004,'Supplier application inverse lacks pending owner evidence.',1;
    INSERT @Historical SELECT e.Ordinal,e.AccountId,e.AccountVersion,e.AccountCode,e.AccountName,e.AccountType,e.AccountPurpose
      FROM Purchasing.SupplierApplicationControl(@TenantId,@InverseApplication) a
      CROSS APPLY(VALUES(1,a.DebtAccountId,a.DebtAccountVersion,a.DebtCode,a.DebtName,a.DebtType,a.DebtPurpose),
        (2,a.FundingAccountId,a.FundingAccountVersion,a.FundingCode,a.FundingName,a.FundingType,a.FundingPurpose))
        e(Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose)
      JOIN @Input l ON l.Ordinal=e.Ordinal AND l.AccountId=e.AccountId AND l.AccountVersion=e.AccountVersion
        AND ((e.Ordinal=1 AND l.Credit=a.Amount AND l.Debit=0) OR (e.Ordinal=2 AND l.Debit=a.Amount AND l.Credit=0))
      WHERE a.PostingDate<=@PostingDate AND @SourceRevision=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.groupId'))
        AND a.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(@SourceSnapshot,'$.amount'));
    IF (SELECT COUNT(*) FROM @Historical)<>2 OR (SELECT COUNT(*) FROM @Input)<>2
      THROW 51004,'Supplier application inverse must preserve historical controls.',1;
  END;
  IF EXISTS(SELECT 1 FROM @Input i LEFT JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK)
    ON a.TenantId=@TenantId AND a.Id=i.AccountId
    WHERE a.Id IS NULL OR (a.ArchivedAtUtc IS NOT NULL AND NOT EXISTS(SELECT 1 FROM @Historical h WHERE h.Ordinal=i.Ordinal)))
     THROW 51004,'An account is unavailable.',1;
  IF EXISTS(SELECT 1 FROM @Input i JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK)
    ON a.TenantId=@TenantId AND a.Id=i.AccountId WHERE a.Version<>i.AccountVersion AND NOT EXISTS(SELECT 1 FROM @Historical h WHERE h.Ordinal=i.Ordinal))
     THROW 51009,'An account revision changed.',1;
  DECLARE @DebitWide decimal(38,4),@CreditWide decimal(38,4),@Maximum decimal(38,4)=999999999999999999999999.9999;
  SELECT @DebitWide=SUM(CONVERT(decimal(38,4),Debit)),@CreditWide=SUM(CONVERT(decimal(38,4),Credit)) FROM @Input;
  IF @DebitWide<>@CreditWide OR @DebitWide<=0 OR @DebitWide>@Maximum OR @CreditWide>@Maximum
     THROW 51000,'Journal must balance within supported totals.',1;
  DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@SourceEventId uniqueidentifier=NEWID(),@JournalId uniqueidentifier=NEWID(),@Sequence bigint;
  INSERT Accounting.SourceEvents(Id,TenantId,SourceKind,SourceId,SourceRevision,EventKind,RuleVersion,ActorId,
    DocumentDate,EffectiveDate,PostingDate,Reference,Reason,SnapshotJson,SnapshotSha256,RecordedAtUtc)
    VALUES(@SourceEventId,@TenantId,@SourceKind,@SourceId,@SourceRevision,@EventKind,@RuleVersion,@ActorId,
      @DocumentDate,@EffectiveDate,@PostingDate,@Reference,@Reason,@SourceSnapshot,
      HASHBYTES('SHA2_256',CONVERT(varbinary(max),@SourceSnapshot)),@Now);
  INSERT Accounting.JournalEntries(Id,TenantId,SourceEventId,ConfigurationVersion,Currency,Scale,
    DocumentDate,EffectiveDate,PostingDate,RecordedAtUtc,ActorId,Reference,Reason,DebitTotal,CreditTotal)
    VALUES(@JournalId,@TenantId,@SourceEventId,@ConfigVersion,@Currency,@Scale,
      @DocumentDate,@EffectiveDate,@PostingDate,@Now,@ActorId,@Reference,@Reason,
      CONVERT(decimal(28,4),@DebitWide),CONVERT(decimal(28,4),@CreditWide));
  SELECT @Sequence=Sequence FROM Accounting.JournalEntries WHERE TenantId=@TenantId AND Id=@JournalId;
  INSERT Accounting.JournalLines(TenantId,JournalId,Ordinal,AccountId,AccountVersion,
    AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit)
    SELECT @TenantId,@JournalId,i.Ordinal,a.Id,COALESCE(h.AccountVersion,a.Version),COALESCE(h.AccountCode,a.Code),
      COALESCE(h.AccountName,a.Name),COALESCE(h.AccountType,a.Type),COALESCE(h.AccountPurpose,a.Purpose),i.Debit,i.Credit
      FROM @Input i JOIN Accounting.Accounts a ON a.TenantId=@TenantId AND a.Id=i.AccountId
      LEFT JOIN @Historical h ON h.Ordinal=i.Ordinal;
  IF NOT EXISTS(SELECT 1 FROM Accounting.PolicyFreezes WHERE TenantId=@TenantId)
    INSERT Accounting.PolicyFreezes(TenantId,ConfigurationVersion,Currency,Scale,FiscalStartMonth,StartApproach,
      StartDate,FirstJournalId,RecordedAtUtc)
      VALUES(@TenantId,@ConfigVersion,@Currency,@Scale,@FiscalMonth,@StartApproach,@StartDate,@JournalId,@Now);
  INSERT Accounting.PostingReceipts(TenantId,RequestId,ActorId,SourceCommandKind,SourceCommandVersion,
    CanonicalInput,InputSha256,SourceEventId,JournalId,Sequence,RecordedAtUtc)
    VALUES(@TenantId,@RequestId,@ActorId,@SourceCommandKind,@SourceCommandVersion,@CanonicalInput,
      HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInput)),@SourceEventId,@JournalId,@Sequence,@Now);
  INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
    VALUES(NEWID(),@TenantId,@ActorId,N'Accounting.PostJournal',N'JournalEntry',@JournalId,@Now);
  SELECT @SourceEventId SourceEventId,@JournalId JournalId,@Sequence Sequence,@Now RecordedAtUtc;
END;
