-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CompleteSupplierCorrectionJournalPlan
  @TenantId uniqueidentifier,@Date date,@Currency varchar(3),@Policy nvarchar(max),@PositionBindings nvarchar(max),
  @ProjectedControls nvarchar(max),@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Members TABLE(MemberKind varchar(40),SourceKind varchar(40),SourceId uniqueidentifier,
    SourceRevisionId uniqueidentifier,OriginalJournalId uniqueidentifier,OwnerMemberId uniqueidentifier,Position int,Lines nvarchar(max));
  INSERT @Members SELECT MemberKind,SourceKind,SourceId,SourceRevisionId,OriginalJournalId,OwnerMemberId,Position,Lines
    FROM OPENJSON(@Plan,'$.proposedJournals') WITH(MemberKind varchar(40) '$.memberKind',SourceKind varchar(40) '$.sourceKind',
      SourceId uniqueidentifier '$.sourceId',SourceRevisionId uniqueidentifier '$.sourceRevisionId',
      OriginalJournalId uniqueidentifier '$.originalJournalId',OwnerMemberId uniqueidentifier '$.ownerMemberId',
      Position int '$.position',Lines nvarchar(max) '$.lines' AS JSON);
  DECLARE @Inverses TABLE(JournalId uniqueidentifier PRIMARY KEY,OwnerMemberId uniqueidentifier);
  INSERT @Inverses SELECT JournalId,SettlementId FROM OPENJSON(@Plan,'$.reverseReleases')
    WITH(JournalId uniqueidentifier '$.journalId',SettlementId uniqueidentifier '$.settlementId');
  INSERT @Inverses SELECT JournalId,NULL FROM OPENJSON(@Plan,'$.sources')
    WITH(SourceKind varchar(40) '$.sourceKind',JournalId uniqueidentifier '$.journalId')
    WHERE COALESCE(SourceKind,'SupplierCredit')='SupplierCredit';
  INSERT @Inverses SELECT JournalId,EventId FROM OPENJSON(@Plan,'$.reverseAdjustments')
    WITH(JournalId uniqueidentifier '$.journalId',EventId uniqueidentifier '$.recognitionEventId');
  INSERT @Inverses SELECT original.JournalId,original.EventId
    FROM OPENJSON(@Plan,'$.units') unit CROSS APPLY OPENJSON(unit.value,'$.originals')
      WITH(JournalId uniqueidentifier '$.journalId',EventId uniqueidentifier '$.eventId') original
    WHERE original.JournalId IS NOT NULL;
  INSERT @Members(MemberKind,SourceKind,SourceId,SourceRevisionId,OriginalJournalId,OwnerMemberId,Lines)
    SELECT 'ReverseSource',source.SourceKind,source.SourceId,source.SourceRevision,original.JournalId,original.OwnerMemberId,
      (SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,
        CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit
        FROM Accounting.JournalInverseLines(@TenantId,original.JournalId) ORDER BY Ordinal FOR JSON PATH)
    FROM @Inverses original JOIN Accounting.JournalEntries journal ON journal.TenantId=@TenantId AND journal.Id=original.JournalId
    JOIN Accounting.SourceEvents source ON source.TenantId=@TenantId AND source.Id=journal.SourceEventId;
  IF @@ROWCOUNT<>(SELECT COUNT(*) FROM @Inverses)
    THROW 51004,'Complete original journal bindings are required.',1;

  DECLARE @Controls TABLE(ItemId uniqueidentifier PRIMARY KEY,AccountId uniqueidentifier,AccountVersion uniqueidentifier);
  INSERT @Controls SELECT ItemId,AccountId,AccountVersion FROM OPENJSON(@ProjectedControls)
    WITH(ItemId uniqueidentifier '$.itemId',AccountId uniqueidentifier '$.accountId',AccountVersion uniqueidentifier '$.accountVersion');
  DECLARE @Receivable uniqueidentifier,@ReceivableVersion uniqueidentifier,@Clearing uniqueidentifier,@ClearingVersion uniqueidentifier;
  SELECT @Receivable=AccountId,@ReceivableVersion=AccountVersion
    FROM Purchasing.SupplierCorrectionControlAccount(@TenantId,@Policy,'SupplierCreditReceivable','Asset');
  SELECT @Clearing=AccountId,@ClearingVersion=AccountVersion
    FROM Purchasing.SupplierCorrectionControlAccount(@TenantId,@Policy,'SupplierRefundClearing','Liability');
  IF @Clearing IS NULL AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.settlements'))
    THROW 51004,'Current supplier refund-clearing mapping unavailable.',1;
  INSERT @Controls SELECT SettlementId,@Clearing,@ClearingVersion FROM OPENJSON(@Plan,'$.settlements')
    WITH(SettlementId uniqueidentifier '$.settlementId');
  DECLARE @Id uniqueidentifier,@Revision uniqueidentifier,@Total decimal(28,4),@Replacement nvarchar(max),@Claims nvarchar(max),@Lines nvarchar(max);
  DECLARE replacements CURSOR LOCAL FAST_FORWARD FOR
    SELECT ReplacementId,ReplacementRevisionId,Total,Replacement FROM OPENJSON(@Plan,'$.sources')
    WITH(SourceKind varchar(40) '$.sourceKind',ReplacementId uniqueidentifier '$.replacementId',
      ReplacementRevisionId uniqueidentifier '$.replacementRevisionId',Total decimal(28,4) '$.replacement.total',
      Replacement nvarchar(max) '$.replacement' AS JSON)
    WHERE COALESCE(SourceKind,'SupplierCredit')='SupplierCredit' AND ReplacementId IS NOT NULL;
  OPEN replacements; FETCH NEXT FROM replacements INTO @Id,@Revision,@Total,@Replacement;
  WHILE @@FETCH_STATUS=0
  BEGIN
    IF @Receivable IS NULL THROW 51004,'Supplier credit account mapping unavailable.',1;
    SET @Claims=(SELECT AccountId accountId,AccountVersion accountVersion,Amount amount FROM OPENJSON(@PositionBindings)
      WITH(CreditId uniqueidentifier '$.creditId',AccountId uniqueidentifier '$.accountId',
        AccountVersion uniqueidentifier '$.accountVersion',Amount decimal(28,4) '$.amount') WHERE CreditId=@Id FOR JSON PATH);
    IF (SELECT COUNT(*) FROM OPENJSON(@Claims))<>(SELECT COUNT(*) FROM OPENJSON(@Replacement,'$.claims'))
      THROW 51004,'Complete successor credit journal bindings are required.',1;
    EXEC Purchasing.BuildSupplierCreditJournalLines @Receivable,@ReceivableVersion,@Total,@Claims,@Lines OUTPUT;
    INSERT @Members(MemberKind,SourceKind,SourceId,SourceRevisionId,Lines) VALUES('Replacement','SupplierCredit',@Id,@Revision,@Lines);
    INSERT @Controls VALUES(@Id,@Receivable,@ReceivableVersion);
    FETCH NEXT FROM replacements INTO @Id,@Revision,@Total,@Replacement;
  END;
  CLOSE replacements; DEALLOCATE replacements;

  DECLARE @Pairs TABLE(MemberKind varchar(40),SourceKind varchar(40),SourceId uniqueidentifier,OriginalJournalId uniqueidentifier,
    OwnerMemberId uniqueidentifier,Position int,FirstId uniqueidentifier,FirstVersion uniqueidentifier,
    SecondId uniqueidentifier,SecondVersion uniqueidentifier,Amount decimal(28,4));
  INSERT @Pairs SELECT 'ReverseApplication','SupplierApplicationReversal',NULL,planned.JournalId,planned.ApplicationId,NULL,
    control.DebtAccountId,control.DebtAccountVersion,control.FundingAccountId,control.FundingAccountVersion,-control.Amount
    FROM OPENJSON(@Plan,'$.applications') WITH(ApplicationId uniqueidentifier '$.applicationId',JournalId uniqueidentifier '$.journalId') planned
    OUTER APPLY Purchasing.SupplierApplicationControl(@TenantId,planned.ApplicationId) control;
  INSERT @Pairs SELECT 'ReleaseSettlement','SupplierRefundRelease',planned.SettlementId,NULL,planned.SettlementId,NULL,
    control.AccountId,control.AccountVersion,@Clearing,@ClearingVersion,planned.Amount
    FROM OPENJSON(@Plan,'$.settlements') WITH(SettlementId uniqueidentifier '$.settlementId',
      FundingId uniqueidentifier '$.fundingItemId',Amount decimal(28,4) '$.amount') planned
    OUTER APPLY Purchasing.SupplierCreditControl(@TenantId,planned.FundingId) control;
  INSERT @Pairs SELECT 'RecognitionAdjustment','SupplierRecognitionCreditAdjustment',adjustment.SourceId,NULL,adjustment.EventId,NULL,
    adjustment.FirstId,adjustment.FirstVersion,adjustment.SecondId,adjustment.SecondVersion,adjustment.Amount
    FROM OPENJSON(@Plan,'$.recognitionAdjustments')
      WITH(SourceId uniqueidentifier '$.sourceId',EventId uniqueidentifier '$.recognitionEventId',Amount decimal(28,4) '$.amount',
        FirstId uniqueidentifier '$.expenseAccountId',FirstVersion uniqueidentifier '$.expenseAccountVersion',
        SecondId uniqueidentifier '$.prepaymentAccountId',SecondVersion uniqueidentifier '$.prepaymentAccountVersion') adjustment;
  DECLARE @Applications TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4));
  INSERT @Applications SELECT CONVERT(int,planned.[key]),target.FundingId,target.DebtId,target.Amount
    FROM OPENJSON(@Plan,'$.reapplications') planned CROSS APPLY OPENJSON(planned.value)
      WITH(FundingId uniqueidentifier '$.fundingItemId',DebtId uniqueidentifier '$.debtItemId',Amount decimal(28,4) '$.amount') target;
  IF EXISTS(SELECT 1 FROM @Applications)
  BEGIN
    INSERT @Controls SELECT required.ItemId,control.AccountId,control.AccountVersion
      FROM (SELECT FundingId ItemId FROM @Applications UNION SELECT DebtId FROM @Applications) required
      CROSS APPLY Purchasing.SupplierItemControl(@TenantId,required.ItemId) control
      WHERE NOT EXISTS(SELECT 1 FROM @Controls projected WHERE projected.ItemId=required.ItemId);
    INSERT @Pairs SELECT 'Application','SupplierApplication',target.FundingId,NULL,target.DebtId,target.Position,
      debt.AccountId,debt.AccountVersion,funding.AccountId,funding.AccountVersion,target.Amount
      FROM @Applications target LEFT JOIN @Controls debt ON debt.ItemId=target.DebtId
      LEFT JOIN @Controls funding ON funding.ItemId=target.FundingId;
  END;
  IF EXISTS(SELECT 1 FROM @Pairs WHERE FirstId IS NULL OR FirstVersion IS NULL OR SecondId IS NULL OR SecondVersion IS NULL OR Amount IS NULL)
    THROW 51004,'Complete proposed supplier control bindings are required.',1;
  INSERT @Members SELECT MemberKind,SourceKind,SourceId,NULL,OriginalJournalId,OwnerMemberId,Position,
    (SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit
      FROM Accounting.ControlJournalPair(pair.FirstId,pair.FirstVersion,pair.SecondId,pair.SecondVersion,pair.Amount)
      ORDER BY Ordinal FOR JSON PATH)
    FROM @Pairs pair;
  IF EXISTS(SELECT 1 FROM @Members WHERE Lines IS NULL OR Lines=N'[]')
    THROW 51004,'Complete proposed journal lines are required.',1;
  DECLARE @Journals nvarchar(max)=(SELECT MemberKind memberKind,SourceKind sourceKind,SourceId sourceId,SourceRevisionId sourceRevisionId,
    OriginalJournalId originalJournalId,OwnerMemberId ownerMemberId,Position position,@Date postingDate,@Currency currency,JSON_QUERY(Lines) lines
    FROM @Members ORDER BY MemberKind,SourceKind,SourceId,SourceRevisionId,OriginalJournalId,OwnerMemberId,Position FOR JSON PATH,INCLUDE_NULL_VALUES);
  SET @Plan=JSON_MODIFY(@Plan,'$.proposedJournals',JSON_QUERY(@Journals));
END;
