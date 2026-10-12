-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.PlanSupplierCorrection
  @TenantId uniqueidentifier,@Command nvarchar(max),@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')) IS NULL
    OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Supplier correction planning requires its owner transaction.',1;
  DECLARE @Canonical nvarchar(max);
  EXEC Purchasing.ValidateSupplierFinancialCommand N'CorrectSupplierPayment',@Command,@Canonical OUTPUT;
  IF (SELECT COUNT(*) FROM OPENJSON(@Canonical) WHERE [key] NOT IN('expectedPlanFingerprint','replacement'))<>11
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
      ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion',
       'supplierId','currency','postingDate','paymentId','expectedPaymentVersion','reason','expectedPlanFingerprint','replacement')
      OR ([key]='schemaVersion' AND type<>2) OR ([key]='replacement' AND type<>5)
      OR ([key] NOT IN('schemaVersion','replacement') AND type<>1))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','paymentId')
      AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedPurchaseOrderVersion','expectedPaymentVersion')
      AND (DATALENGTH(value)<>36 OR TRY_CONVERT(binary(8),value,1) IS NULL))
    OR DATALENGTH(JSON_VALUE(@Canonical,'$.postingDate'))<>20 OR TRY_CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23) IS NULL
    OR DATALENGTH(JSON_VALUE(@Canonical,'$.currency'))<>6
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key]='reason' AND (DATALENGTH(value)>4000 OR LEN(TRIM(value))=0))
    THROW 51000,'Invalid supplier correction fields.',1;
  DECLARE @Payment uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Journal uniqueidentifier,@Source uniqueidentifier,
    @Revision uniqueidentifier,@Version binary(8),@Amount decimal(28,4),@SourceDate date,@SnapshotHash binary(32);
  IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po AND State='Ordered' AND IsDeleted=0
      AND SupplierId=@Supplier AND Currency=JSON_VALUE(@Canonical,'$.currency') AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
    THROW 51009,'Purchase revision changed.',1;
  SELECT @Version=v.RowVersion,@Amount=p.Amount,@Revision=p.RevisionId,@SourceDate=p.PostingDate
    FROM Purchasing.SupplierPayments p WITH(UPDLOCK,HOLDLOCK)
    JOIN Purchasing.SupplierPaymentVersions v WITH(UPDLOCK,HOLDLOCK) ON v.TenantId=p.TenantId AND v.PaymentId=p.Id
    WHERE p.TenantId=@TenantId AND p.Id=@Payment AND p.SupplierId=@Supplier AND p.PurchaseOrderId=@Po AND p.Currency=JSON_VALUE(@Canonical,'$.currency');
  IF @Version IS NULL OR @Version<>CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPaymentVersion'),1)
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections WHERE TenantId=@TenantId AND OriginalPaymentId=@Payment)
    THROW 51009,'Supplier payment revision changed.',1;
  IF NOT EXISTS(SELECT 1 FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config)
    THROW 51009,'Accounting configuration changed.',1;
  IF @Date<@SourceDate OR EXISTS(SELECT 1 FROM Accounting.Configurations WHERE TenantId=@TenantId
      AND @Date<TRY_CONVERT(date,JSON_VALUE(Payload,'$.policies.plannedStartDate'),23))
    THROW 51004,'Correction date precedes its source or accounting start.',1;
  IF EXISTS(SELECT 1 FROM Accounting.PeriodClosures WHERE TenantId=@TenantId AND PeriodStart=DATEFROMPARTS(YEAR(@Date),MONTH(@Date),1))
    THROW 51009,'The posting period is closed.',1;
  IF (SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl(@TenantId,@Payment))<>1
    THROW 51004,'Complete historical payment evidence is unavailable.',1;
  SELECT @Journal=j.Id,@Source=s.Id,@SnapshotHash=s.SnapshotSha256 FROM Accounting.SourceEvents s
    JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id
    WHERE s.TenantId=@TenantId AND s.SourceKind='SupplierPayment' AND s.SourceId=@Payment AND s.SourceRevision=@Revision AND s.EventKind='Payment';
  DECLARE @EventCount bigint=1+(SELECT COUNT_BIG(*) FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND FundingItemId=@Payment)
    +(SELECT COUNT_BIG(*) FROM Purchasing.SupplierApplicationReversals r JOIN Purchasing.SupplierApplications a
      ON a.TenantId=r.TenantId AND a.Id=r.ApplicationId WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment);
  IF @EventCount>1000 THROW 51000,'Supplier correction closure exceeds 1000 events.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierApplications a WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment
      AND (a.PostingDate>@Date OR (SELECT COUNT(*) FROM Purchasing.SupplierApplicationControl(@TenantId,a.Id))<>1))
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals r JOIN Purchasing.SupplierApplications a ON a.TenantId=r.TenantId AND a.Id=r.ApplicationId
      WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment AND r.PostingDate>@Date)
    OR EXISTS(SELECT 1 FROM Accounting.CorrectionGroups WHERE TenantId=@TenantId AND OriginalJournalId=@Journal)
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements WHERE TenantId=@TenantId AND ItemId=@Payment AND EventKind NOT IN('Open','Apply','ReverseApplication'))
    THROW 51009,'Supplier correction has an unsupported or later dependency.',1;
  DECLARE @Items TABLE(Id uniqueidentifier PRIMARY KEY);
  INSERT @Items SELECT @Payment UNION SELECT DebtItemId FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND FundingItemId=@Payment;
  DECLARE @Replacement nvarchar(max)=JSON_QUERY(@Canonical,'$.replacement'),@ReplacementCanonical nvarchar(max),@ReplacementAccounts nvarchar(max);
  IF @Replacement IS NOT NULL
  BEGIN
    EXEC Purchasing.ValidateSupplierFinancialCommand N'RecordSupplierPayment',@Replacement,@ReplacementCanonical OUTPUT;
    DECLARE @PaymentInput nvarchar(max)=@ReplacementCanonical;    IF (SELECT COUNT(*) FROM OPENJSON(@PaymentInput))<>20
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
        ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion','supplierId','currency',
         'postingDate','paymentId','paymentRevisionId','paymentDate','effectiveDate','amount','method','fundingAccountId',
         'expectedFundingAccountVersion','reference','notes','evidence','allocations')
        OR ([key]='schemaVersion' AND type<>2) OR ([key]='allocations' AND type<>4) OR ([key]='evidence' AND type<>5)
        OR ([key] IN('reference','notes') AND type NOT IN(0,1))
        OR ([key] NOT IN('schemaVersion','allocations','evidence','reference','notes') AND type<>1))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput,'$.allocations') e WHERE e.type<>5
        OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>4
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('billId','itemId','expectedItemVersion','amount') OR p.type<>1))
      THROW 51000,'Invalid supplier payment fields.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','paymentId','paymentRevisionId','fundingAccountId','expectedFundingAccountVersion')
        AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
      OR DATALENGTH(JSON_VALUE(@PaymentInput,'$.expectedPurchaseOrderVersion'))<>36
      OR TRY_CONVERT(binary(8),JSON_VALUE(@PaymentInput,'$.expectedPurchaseOrderVersion'),1) IS NULL
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput,'$.allocations') e CROSS APPLY OPENJSON(e.value) p WHERE
        (p.[key]='billId' AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
        OR (p.[key]='expectedItemVersion' AND (DATALENGTH(p.value)<>36 OR TRY_CONVERT(binary(8),p.value,1) IS NULL))
        OR (p.[key]='amount' AND TRY_CONVERT(decimal(28,4),p.value)<=0))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] IN('postingDate','paymentDate','effectiveDate')
        AND (DATALENGTH(value)<>20 OR TRY_CONVERT(date,value,23) IS NULL))
      OR DATALENGTH(JSON_VALUE(@PaymentInput,'$.currency'))<>6
      OR TRY_CONVERT(decimal(28,4),JSON_VALUE(@PaymentInput,'$.amount'))<=0
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key]='method' AND value COLLATE Latin1_General_100_BIN2 NOT IN('Bank','Cash'))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE ([key]='reference' AND DATALENGTH(value)>400) OR ([key]='notes' AND DATALENGTH(value)>4000))
      THROW 51000,'Invalid supplier payment values.',1;    DECLARE @EvidenceInput nvarchar(max)=JSON_QUERY(@ReplacementCanonical,'$.evidence'),@ReplacementDocuments nvarchar(max);    IF (SELECT COUNT(*) FROM OPENJSON(@EvidenceInput))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('documents','missingEvidenceReason')
        OR ([key]='documents' AND type<>4) OR ([key]='missingEvidenceReason' AND type NOT IN(0,1)))
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key]='missingEvidenceReason' AND DATALENGTH(value)>4000)
      OR (NOT EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents')) AND NULLIF(TRIM(JSON_VALUE(@EvidenceInput,'$.missingEvidenceReason')),'') IS NULL)
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents') d WHERE d.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(d.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(d.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('documentId','revisionId')
          OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
      THROW 51000,'Invalid supplier payment evidence.',1;    IF (SELECT COUNT(*) FROM OPENJSON(@ReplacementCanonical))<>20
      OR JSON_VALUE(@ReplacementCanonical,'$.purchaseOrderId')<>CONVERT(nvarchar(36),@Po)
      OR JSON_VALUE(@ReplacementCanonical,'$.supplierId')<>CONVERT(nvarchar(36),@Supplier)
      OR JSON_VALUE(@ReplacementCanonical,'$.currency')<>JSON_VALUE(@Canonical,'$.currency')
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedConfigurationVersion')<>CONVERT(nvarchar(36),@Config)
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedPurchaseOrderVersion')<>JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion')
      OR TRY_CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.amount')) IS NULL
      OR TRY_CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.amount'))<=0
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical,'$.allocations') e
        WHERE (SELECT COUNT(*) FROM OPENJSON(e.value))<>4 OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems i
          JOIN Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK) ON v.TenantId=i.TenantId AND v.ItemId=i.Id
          WHERE i.TenantId=@TenantId AND i.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId')) AND i.Kind='Payable'
            AND i.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.billId')) AND i.SupplierId=@Supplier AND i.PurchaseOrderId=@Po
            AND v.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(e.value,'$.expectedItemVersion'),1)))
      THROW 51009,'Replacement identity or target revision changed.',1;
    DECLARE @ReplacementDate date=TRY_CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.postingDate'),23),
      @ReplacementAmount decimal(28,4)=TRY_CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.amount')),
      @ReplacementFunding uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.fundingAccountId')),
      @ReplacementAdvance uniqueidentifier,@ReplacementPayload nvarchar(max),@ReplacementScale int;
    SELECT @ReplacementPayload=Payload FROM Accounting.Configurations WHERE TenantId=@TenantId AND Version=@Config;
    SELECT @ReplacementAdvance=TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')) FROM OPENJSON(@ReplacementPayload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='SupplierAdvance';
    SET @ReplacementScale=TRY_CONVERT(int,JSON_VALUE(@ReplacementPayload,'$.policies.scale'));
    IF @ReplacementDate IS NULL OR @ReplacementDate<TRY_CONVERT(date,JSON_VALUE(@ReplacementPayload,'$.policies.plannedStartDate'),23)
      OR TRY_CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.paymentDate'),23)>CONVERT(date,SYSUTCDATETIME())
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical) WHERE [key] IN('paymentDate','effectiveDate')
        AND (TRY_CONVERT(date,value,23) IS NULL OR TRY_CONVERT(date,value,23)>@ReplacementDate))
      OR @ReplacementAmount<>ROUND(@ReplacementAmount,@ReplacementScale,1)
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical,'$.allocations') e WHERE TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount')) IS NULL
        OR TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount'))<=0
        OR TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount'))<>ROUND(TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount')),@ReplacementScale,1))
      THROW 51000,'Replacement dates or amounts are invalid.',1;
    IF EXISTS(SELECT 1 FROM Accounting.PeriodClosures WHERE TenantId=@TenantId AND PeriodStart=DATEFROMPARTS(YEAR(@ReplacementDate),MONTH(@ReplacementDate),1))
      THROW 51009,'The replacement posting period is closed.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierPayments WHERE TenantId=@TenantId AND
        (Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId')) OR RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentRevisionId'))))
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId
        AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId')))
      OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@ReplacementFunding
        AND Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.expectedFundingAccountVersion')))
      OR COALESCE((SELECT SUM(TRY_CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))) FROM OPENJSON(@ReplacementCanonical,'$.allocations')),0)>@ReplacementAmount
      THROW 51009,'Replacement source, funding revision or allocation capacity changed.',1;
    IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@ReplacementFunding AND ArchivedAtUtc IS NULL AND Type='Asset' AND Purpose IN('Bank','Cash'))
      OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@ReplacementAdvance AND ArchivedAtUtc IS NULL AND Type='Asset' AND Purpose='SupplierAdvance')
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical,'$.allocations') e JOIN Purchasing.SupplierOpenItems i ON i.TenantId=@TenantId AND i.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId'))
        WHERE i.SourcePostingDate>@ReplacementDate OR (SELECT COUNT(*) FROM Purchasing.SupplierItemControl(@TenantId,i.Id))<>1)
      THROW 51004,'Replacement funding or historical target control is unavailable.',1;
    SET @ReplacementAccounts=(SELECT Id id,Version version,Code code,Name name,Type type,Purpose purpose FROM Accounting.Accounts
      WHERE TenantId=@TenantId AND Id IN(@ReplacementFunding,@ReplacementAdvance) ORDER BY Id FOR JSON PATH);
    INSERT @Items SELECT DISTINCT TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId')) FROM OPENJSON(@ReplacementCanonical,'$.allocations') e
      WHERE NOT EXISTS(SELECT 1 FROM @Items i WHERE i.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId')));
  END;
  DECLARE @ApplicationJson nvarchar(max)=(SELECT a.Id id,CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) version,
      a.DebtItemId debtItemId,CONVERT(nvarchar(60),a.Amount) amount,a.PostingDate postingDate,
      a.GroupId groupId,c.Embedded embedded,c.JournalId journalId,applicationSource.Id sourceEventId,
      applicationSource.SourceRevision sourceRevision,CONVERT(varchar(64),applicationSource.SnapshotSha256,2) sourceSnapshotSha256,
      r.Id reversalId,r.GroupId reversalGroupId,r.PostingDate reversalDate,
      rj.Id reversalJournalId,CONVERT(varchar(64),rs.SnapshotSha256,2) reversalSnapshotSha256
    FROM Purchasing.SupplierApplications a JOIN Purchasing.SupplierApplicationVersions v WITH(UPDLOCK,HOLDLOCK)
      ON v.TenantId=a.TenantId AND v.ApplicationId=a.Id
    CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,a.Id) c
    JOIN Accounting.JournalEntries applicationJournal ON applicationJournal.TenantId=a.TenantId AND applicationJournal.Id=c.JournalId
    JOIN Accounting.SourceEvents applicationSource ON applicationSource.TenantId=a.TenantId AND applicationSource.Id=applicationJournal.SourceEventId
    LEFT JOIN Purchasing.SupplierApplicationReversals r ON r.TenantId=a.TenantId AND r.ApplicationId=a.Id
    LEFT JOIN Accounting.SourceEvents rs ON rs.TenantId=r.TenantId AND rs.SourceId=r.Id AND rs.SourceKind='SupplierApplicationReversal'
      AND rs.SourceRevision=r.GroupId AND rs.EventKind='Reverse' AND rs.PostingDate=r.PostingDate AND rs.RecordedAtUtc=r.RecordedAtUtc
    LEFT JOIN Accounting.JournalEntries rj ON rj.TenantId=rs.TenantId AND rj.SourceEventId=rs.Id
    WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment ORDER BY a.Id FOR JSON PATH,INCLUDE_NULL_VALUES),
    @ItemJson nvarchar(max)=(SELECT v.ItemId id,CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) version
      FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK) JOIN @Items i ON i.Id=v.ItemId WHERE v.TenantId=@TenantId ORDER BY v.ItemId FOR JSON PATH),
    @Effects nvarchar(max)=(SELECT itemId,CONVERT(nvarchar(60),SUM(amount)) amount FROM(
      SELECT @Payment itemId,CONVERT(decimal(38,4),-@Amount) amount
      UNION ALL SELECT e.ItemId,CONVERT(decimal(38,4),a.Amount) FROM Purchasing.SupplierApplications a
        CROSS APPLY(VALUES(a.FundingItemId),(a.DebtItemId)) e(ItemId)
        WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment AND NOT EXISTS(
          SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=a.TenantId AND r.ApplicationId=a.Id)
      UNION ALL SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId')),
        TRY_CONVERT(decimal(38,4),JSON_VALUE(@ReplacementCanonical,'$.amount')) WHERE @ReplacementCanonical IS NOT NULL
      UNION ALL SELECT e.ItemId,-TRY_CONVERT(decimal(38,4),JSON_VALUE(a.value,'$.amount'))
        FROM OPENJSON(@ReplacementCanonical,'$.allocations') a CROSS APPLY(VALUES(
          TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId'))),
          (TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId')))) e(ItemId)
    ) effects GROUP BY itemId ORDER BY itemId FOR JSON PATH);
  -- Document/storage locks follow the complete source and item/application coordination set.
  IF @Replacement IS NOT NULL
    DECLARE @InheritedEvidence uniqueidentifier=(SELECT Id FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND OwnerKind='SupplierPayment' AND OwnerId=@Payment);
EXEC Accounting.ValidateInheritedFinancialEvidence @TenantId,@Po,@EvidenceInput,@InheritedEvidence,@ReplacementDocuments OUTPUT;
  SET @Canonical=JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL);
  IF EXISTS(SELECT 1 FROM OPENJSON(@ApplicationJson) a WHERE JSON_VALUE(a.value,'$.reversalId') IS NOT NULL
      AND (JSON_VALUE(a.value,'$.reversalJournalId') IS NULL OR EXISTS(SELECT 1 FROM Accounting.CorrectionGroups
        WHERE TenantId=@TenantId AND OriginalJournalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.reversalJournalId')))))
    OR EXISTS(SELECT 1 FROM OPENJSON(@ApplicationJson) a JOIN Accounting.CorrectionGroups c
      ON c.TenantId=@TenantId AND c.OriginalJournalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.journalId')))
    THROW 51009,'Supplier reversal has an unsupported external dependency.',1;
  -- Validate the complete dated proposal without persisting a group or testing
  -- an intermediate compensation in isolation. Retained unapplications cancel
  -- the corresponding original inverse effects, leaving active applications.
  DECLARE @PreviewGroup uniqueidentifier=NEWID(),@ProposedMovements nvarchar(max);
  SET @ProposedMovements=(SELECT itemId,@PreviewGroup groupId,postingDate,CONVERT(nvarchar(60),amount) amount FROM(
    SELECT @Payment itemId,@Date postingDate,CONVERT(decimal(28,4),-@Amount) amount
    UNION ALL SELECT e.ItemId,@Date,a.Amount FROM Purchasing.SupplierApplications a
      CROSS APPLY(VALUES(a.FundingItemId),(a.DebtItemId)) e(ItemId)
      WHERE a.TenantId=@TenantId AND a.FundingItemId=@Payment AND NOT EXISTS(
        SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=a.TenantId AND r.ApplicationId=a.Id)
    UNION ALL SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId')),@ReplacementDate,@ReplacementAmount
      WHERE @ReplacementCanonical IS NOT NULL
    UNION ALL SELECT e.ItemId,@ReplacementDate,-TRY_CONVERT(decimal(28,4),JSON_VALUE(a.value,'$.amount'))
      FROM OPENJSON(@ReplacementCanonical,'$.allocations') a CROSS APPLY(VALUES(
        TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId'))),
        (TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId')))) e(ItemId)
  ) proposed FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@ProposedMovements;
  SET @Plan=(SELECT 1 schemaVersion,@EventCount eventCount,@Payment paymentId,@Revision paymentRevisionId,
    CONVERT(varchar(18),@Version,1) paymentVersion,@Journal journalId,@Source sourceEventId,CONVERT(varchar(64),@SnapshotHash,2) sourceSnapshotSha256,
    @Date postingDate,JSON_QUERY(@Canonical) command,JSON_QUERY(@ApplicationJson) applications,JSON_QUERY(@ItemJson) items,
    JSON_QUERY(@Effects) effects,JSON_QUERY(@ReplacementAccounts) replacementAccounts FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  DECLARE @Fingerprint varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Plan)),2);
  SET @Plan=JSON_MODIFY(@Plan,'$.fingerprint',@Fingerprint);
END;
