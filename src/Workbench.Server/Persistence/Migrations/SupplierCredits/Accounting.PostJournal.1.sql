-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Accounting].[PostJournal]
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
  @RequiredPermission nvarchar(max),@SourceCommandKind nvarchar(max),@SourceCommandVersion int,
  @CanonicalInput nvarchar(max),@SourceKind nvarchar(max),@SourceId uniqueidentifier,
  @SourceRevision uniqueidentifier,@EventKind nvarchar(max),@RuleVersion int,
  @ExpectedConfigurationVersion uniqueidentifier,@Currency nvarchar(max),
  @DocumentDate date,@EffectiveDate date,@PostingDate date,
  @Reference nvarchar(max)=NULL,@Reason nvarchar(max)=NULL,
  @SourceSnapshot nvarchar(max),@Lines nvarchar(max),@SupplierControlItems nvarchar(max)=NULL
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
  DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
  IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
    THROW 51003,'Current tenant and actor authority are required.',1;
  IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
    THROW 51000,'A bounded source permission is required.',1;
  -- Callers acquire this same lock before their source-state checks. Reacquiring is transaction-owned.
  DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
  IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
  BEGIN TRY
    EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;
  END TRY
  BEGIN CATCH
    IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1;
    THROW;
  END CATCH;
  IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
    OR @SourceCommandKind IS NULL OR DATALENGTH(@SourceCommandKind) NOT BETWEEN 2 AND 128
    OR DATALENGTH(@SourceCommandKind)<>DATALENGTH(RTRIM(@SourceCommandKind))
    OR @SourceCommandKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
    OR @SourceCommandVersion IS NULL OR @SourceCommandVersion<1
    OR @CanonicalInput IS NULL OR DATALENGTH(@CanonicalInput)>262144 OR ISJSON(@CanonicalInput,OBJECT)<>1
    THROW 51000,'Invalid source command.',1;
  IF EXISTS(SELECT 1 FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
      AND ActorId=@ActorId AND CONVERT(varbinary(max),SourceCommandKind)=CONVERT(varbinary(max),@SourceCommandKind)
      AND SourceCommandVersion=@SourceCommandVersion AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput))
      THROW 51009,'This request identifier was already used with different content.',1;
    SELECT SourceEventId,JournalId,Sequence,RecordedAtUtc
      FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
    RETURN;
  END;
  IF @SourceKind IS NULL OR DATALENGTH(@SourceKind) NOT BETWEEN 2 AND 128
    OR @EventKind IS NULL OR DATALENGTH(@EventKind) NOT BETWEEN 2 AND 128
    OR DATALENGTH(@SourceKind)<>DATALENGTH(RTRIM(@SourceKind))
    OR DATALENGTH(@EventKind)<>DATALENGTH(RTRIM(@EventKind))
    OR @SourceKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
    OR @EventKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
    OR @SourceId IS NULL OR @SourceId='00000000-0000-0000-0000-000000000000'
    OR @SourceRevision IS NULL OR @SourceRevision='00000000-0000-0000-0000-000000000000'
    OR @RuleVersion IS NULL OR @RuleVersion<1
    OR @ExpectedConfigurationVersion IS NULL OR @DocumentDate IS NULL OR @EffectiveDate IS NULL OR @PostingDate IS NULL
    OR @Currency IS NULL OR DATALENGTH(@Currency)<>6
    OR DATALENGTH(@Reference)>400 OR DATALENGTH(@Reason)>4000
    OR @SourceSnapshot IS NULL OR DATALENGTH(@SourceSnapshot)>262144 OR ISJSON(@SourceSnapshot,OBJECT)<>1
    OR @Lines IS NULL OR DATALENGTH(@Lines)>1048576 OR ISJSON(@Lines,ARRAY)<>1
    THROW 51000,'Invalid journal evidence or dates.',1;
  DECLARE @ExistingSourceEventId uniqueidentifier;
  SELECT @ExistingSourceEventId=Id FROM Accounting.SourceEvents WITH(UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND SourceKind=@SourceKind COLLATE Latin1_General_100_BIN2
      AND SourceId=@SourceId AND SourceRevision=@SourceRevision
      AND EventKind=@EventKind COLLATE Latin1_General_100_BIN2;
  IF @ExistingSourceEventId IS NOT NULL
  BEGIN
    DECLARE @SourceConflictMessage nvarchar(2048)=N'Source event already posted: '
      +CONVERT(nvarchar(36),@ExistingSourceEventId)+N'.';
    THROW 51009,@SourceConflictMessage,1;
  END;
  DECLARE @ConfigPayload nvarchar(max),@ConfigVersion uniqueidentifier;
  SELECT @ConfigPayload=Payload,@ConfigVersion=Version FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId;
  IF @ConfigPayload IS NULL THROW 51000,'Accounting configuration is incomplete.',1;
  IF @ConfigVersion<>@ExpectedConfigurationVersion
    THROW 51009,'Accounting configuration changed.',1;
  DECLARE @ConfiguredCurrency nvarchar(max)=JSON_VALUE(@ConfigPayload,'$.policies.currency'),
    @Scale int=TRY_CONVERT(int,JSON_VALUE(@ConfigPayload,'$.policies.scale')),
    @FiscalMonth int=TRY_CONVERT(int,JSON_VALUE(@ConfigPayload,'$.policies.fiscalStartMonth')),
    @StartApproach nvarchar(max)=JSON_VALUE(@ConfigPayload,'$.policies.startApproach'),
    @StartDate date=TRY_CONVERT(date,JSON_VALUE(@ConfigPayload,'$.policies.plannedStartDate'),23);
  IF @ConfiguredCurrency IS NULL OR @ConfiguredCurrency COLLATE Latin1_General_100_BIN2<>@Currency COLLATE Latin1_General_100_BIN2
    OR @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR @FiscalMonth IS NULL OR @FiscalMonth NOT BETWEEN 1 AND 12
    OR @StartApproach IS NULL OR @StartDate IS NULL OR @PostingDate<@StartDate
    THROW 51000,'Accounting policy is incomplete or the posting is before its start date.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@SourceSnapshot) GROUP BY [key] HAVING COUNT(*)>1)
     OR EXISTS(SELECT 1 FROM OPENJSON(@CanonicalInput) GROUP BY [key] HAVING COUNT(*)>1)
     THROW 51000,'Duplicate evidence fields are not allowed.',1;
  EXEC Accounting.EnsureOpenPeriod @PostingDate=@PostingDate,@ExpectedConfigurationVersion=@ExpectedConfigurationVersion;
  DECLARE @Count int=(SELECT COUNT(*) FROM OPENJSON(@Lines));
  IF @Count NOT BETWEEN 2 AND 1000 OR EXISTS(SELECT 1 FROM OPENJSON(@Lines) WHERE [type]<>5)
     THROW 51000,'A journal requires two to 1000 lines.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Lines) line WHERE
      (SELECT COUNT(*) FROM OPENJSON(line.value))<>5 OR
      EXISTS(SELECT [key] FROM OPENJSON(line.value) GROUP BY [key] HAVING COUNT(*)>1) OR
      EXISTS(SELECT 1 FROM OPENJSON(line.value) p WHERE DATALENGTH(p.[key])<>DATALENGTH(RTRIM(p.[key]))
        OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN ('ordinal','accountId','accountVersion','debit','credit')
        OR (p.[key] COLLATE Latin1_General_100_BIN2='ordinal' AND p.[type]<>2)
        OR (p.[key] COLLATE Latin1_General_100_BIN2<>'ordinal' AND p.[type]<>1)))
     THROW 51000,'Invalid journal line shape.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Lines) line CROSS APPLY OPENJSON(line.value) p
     WHERE p.[key] COLLATE Latin1_General_100_BIN2 IN ('accountId','accountVersion')
       AND (DATALENGTH(p.[value])<>72 OR TRY_CONVERT(uniqueidentifier,p.[value]) IS NULL))
     THROW 51000,'Invalid account identity.',1;
  DECLARE @Input TABLE(Ordinal int NULL,AccountId uniqueidentifier,AccountVersion uniqueidentifier,
     DebitText nvarchar(max),CreditText nvarchar(max),Debit decimal(28,4),Credit decimal(28,4));
  INSERT @Input(Ordinal,AccountId,AccountVersion,DebitText,CreditText)
    SELECT TRY_CONVERT(int,JSON_VALUE(value,'$.ordinal')),
      TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')),
      TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountVersion')),
      JSON_VALUE(value,'$.debit'),JSON_VALUE(value,'$.credit') FROM OPENJSON(@Lines);
  IF EXISTS(SELECT 1 FROM @Input WHERE Ordinal IS NULL OR Ordinal NOT BETWEEN 1 AND @Count OR AccountId IS NULL OR AccountVersion IS NULL
      OR DebitText IS NULL OR CreditText IS NULL OR LEN(DebitText) NOT BETWEEN 1 AND 29 OR LEN(CreditText) NOT BETWEEN 1 AND 29
      OR DATALENGTH(DebitText)<>DATALENGTH(RTRIM(DebitText))
      OR DATALENGTH(CreditText)<>DATALENGTH(RTRIM(CreditText))
      OR DebitText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
      OR CreditText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
      OR LEN(DebitText)-LEN(REPLACE(DebitText,'.',''))>1 OR LEN(CreditText)-LEN(REPLACE(CreditText,'.',''))>1
      OR LEFT(DebitText,1)='.' OR LEFT(CreditText,1)='.' OR RIGHT(DebitText,1)='.' OR RIGHT(CreditText,1)='.'
      OR LEN(LEFT(DebitText,CHARINDEX('.',DebitText+'.')-1))>24
      OR LEN(LEFT(CreditText,CHARINDEX('.',CreditText+'.')-1))>24
      OR LEN(SUBSTRING(DebitText,CHARINDEX('.',DebitText+'.')+1,50))>4
      OR LEN(SUBSTRING(CreditText,CHARINDEX('.',CreditText+'.')+1,50))>4
      OR TRY_CONVERT(decimal(28,4),DebitText) IS NULL OR TRY_CONVERT(decimal(28,4),CreditText) IS NULL
      OR TRY_CONVERT(decimal(28,4),DebitText)<>ROUND(TRY_CONVERT(decimal(28,4),DebitText),@Scale,1)
      OR TRY_CONVERT(decimal(28,4),CreditText)<>ROUND(TRY_CONVERT(decimal(28,4),CreditText),@Scale,1))
      OR EXISTS(SELECT Ordinal FROM @Input GROUP BY Ordinal HAVING COUNT(*)>1)
      THROW 51000,'Journal amounts or ordinals are invalid.',1;
  UPDATE @Input SET Debit=CONVERT(decimal(28,4),DebitText),Credit=CONVERT(decimal(28,4),CreditText);
  IF EXISTS(SELECT 1 FROM @Input WHERE (Debit>0 AND Credit>0) OR (Debit=0 AND Credit=0) OR Debit<0 OR Credit<0)
     THROW 51000,'Exactly one positive amount is required on each line.',1;
  
  DECLARE @Historical TABLE(Ordinal int PRIMARY KEY,AccountId uniqueidentifier,AccountVersion uniqueidentifier,
    AccountCode nvarchar(32),AccountName nvarchar(160),AccountType nvarchar(40),AccountPurpose nvarchar(40));
  IF @SourceCommandKind=N'Supplier.Credit' AND @RequiredPermission=N'SupplierCreditsCorrect'
  BEGIN
    DECLARE @CreditParent uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.correctionRequestId')),
      @CreditParentGroup uniqueidentifier,@CreditParentCorrection uniqueidentifier,@CreditParentTime datetimeoffset,@CreditParentPlan nvarchar(max);
    EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@CreditParent,N'ReplacementCredit',@SourceId,@CanonicalInput,
      @CreditParentGroup OUTPUT,@CreditParentCorrection OUTPUT,@CreditParentTime OUTPUT,@CreditParentPlan OUTPUT;
    IF @CreditParentGroup<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.groupId'))
      OR @CreditParentCorrection<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.correctionId'))
      THROW 51004,'Credit replacement belongs to another source owner.',1;
  END;
  IF @SourceCommandKind=N'Supplier.Credit'
  BEGIN
    IF @SourceKind<>N'SupplierCredit' OR @EventKind<>N'Credit' OR @RequiredPermission NOT IN(N'SupplierCreditsPost',N'SupplierCreditsCorrect')
      OR @SupplierControlItems IS NOT NULL
      OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
      OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCredits c
        JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=c.TenantId AND g.Id=c.GroupId
          AND (g.Operation='RecordCredit' AND g.SourceId=c.Id OR g.Operation='CorrectCredit' AND EXISTS(
        SELECT 1 FROM Purchasing.SupplierSourceCorrections parent WHERE parent.TenantId=c.TenantId AND parent.GroupId=c.GroupId
          AND parent.ReplacementSourceId=c.Id AND parent.ReplacementSourceRevisionId=c.RevisionId
          AND parent.ActorId=c.ActorId AND parent.RecordedAtUtc=c.RecordedAtUtc)) AND g.RecordedAtUtc=c.RecordedAtUtc
        JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.GroupId=c.GroupId
          AND receipt.RequestId=@RequestId AND receipt.Operation='PostSupplierCredit' AND receipt.ActorId=c.ActorId
          AND receipt.RecordedAtUtc=c.RecordedAtUtc AND JSON_VALUE(receipt.ResultJson,'$.state')='pending'
          AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput)
        WHERE c.TenantId=@TenantId AND c.Id=@SourceId AND c.RevisionId=@SourceRevision AND c.ActorId=@ActorId
          AND c.Currency=@Currency AND c.PostingDate=@PostingDate AND c.DocumentDate=@DocumentDate AND c.EffectiveDate=@EffectiveDate
          AND CONVERT(varbinary(max),c.SourceSnapshotJson)=CONVERT(varbinary(max),@SourceSnapshot)
          AND CONVERT(varbinary(max),JSON_QUERY(c.SourceSnapshotJson,'$.command'))=CONVERT(varbinary(max),@CanonicalInput)
          AND EXISTS(SELECT 1 FROM @Input i JOIN Accounting.Accounts account ON account.TenantId=c.TenantId AND account.Id=i.AccountId
            WHERE i.Ordinal=1 AND i.Debit=c.Total AND i.Credit=0 AND account.Type='Asset' AND account.Purpose='SupplierCreditReceivable'))
      THROW 51004,'Authentic pending supplier credit owner required.',1;
    INSERT @Historical
      SELECT DISTINCT input.Ordinal,line.AccountId,line.AccountVersion,line.AccountCode,line.AccountName,line.AccountType,line.AccountPurpose
      FROM @Input input
      JOIN Purchasing.SupplierCreditClaims claim ON claim.TenantId=@TenantId AND claim.CreditId=@SourceId
        AND claim.AccountId=input.AccountId AND claim.AccountVersion=input.AccountVersion
      JOIN Accounting.JournalLines line ON line.TenantId=claim.TenantId AND line.JournalId=claim.OriginalJournalId
        AND line.Ordinal=claim.OriginalJournalOrdinal AND line.AccountId=claim.AccountId AND line.AccountVersion=claim.AccountVersion
      WHERE input.Debit=0 AND input.Credit=(SELECT SUM(CONVERT(decimal(38,4),c.Amount)) FROM Purchasing.SupplierCreditClaims c
        WHERE c.TenantId=@TenantId AND c.CreditId=@SourceId AND c.AccountId=input.AccountId AND c.AccountVersion=input.AccountVersion)
        AND line.Debit>0 AND line.Credit=0 AND line.AccountPurpose='General'
        AND ((claim.Treatment='Expense' AND line.AccountType='Expense') OR (claim.Treatment='Prepayment' AND line.AccountType='Asset'));
    INSERT @Historical
      SELECT DISTINCT input.Ordinal,expense.AccountId,expense.AccountVersion,expense.AccountCode,expense.AccountName,expense.AccountType,expense.AccountPurpose
      FROM @Input input JOIN Purchasing.SupplierCreditClaims claim ON claim.TenantId=@TenantId AND claim.CreditId=@SourceId
        AND claim.AccountId=input.AccountId AND claim.AccountVersion=input.AccountVersion AND claim.Treatment='Expense'
        AND claim.OriginalJournalId IS NULL AND claim.OriginalJournalOrdinal IS NULL
      JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=claim.TenantId AND matched.InvoiceEventId=claim.RecognitionEventId
      CROSS APPLY Purchasing.SupplierRecognitionExpenseControl(@TenantId,matched.RecognitionEventId) expense
      WHERE expense.JournalId IS NULL AND expense.AccountId=input.AccountId AND expense.AccountVersion=input.AccountVersion
        AND input.Debit=0 AND input.Credit=(SELECT SUM(CONVERT(decimal(38,4),other.Amount)) FROM Purchasing.SupplierCreditClaims other
          WHERE other.TenantId=@TenantId AND other.CreditId=@SourceId AND other.AccountId=input.AccountId AND other.AccountVersion=input.AccountVersion)
        AND NOT EXISTS(SELECT 1 FROM @Historical proved WHERE proved.Ordinal=input.Ordinal);
    IF (SELECT COUNT(*) FROM @Historical)<>(SELECT COUNT(*) FROM @Input WHERE Credit>0)
      OR (SELECT COUNT(*) FROM @Input WHERE Debit>0)<>1
      OR (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM Purchasing.SupplierCreditClaims WHERE TenantId=@TenantId AND CreditId=@SourceId)
        <>(SELECT SUM(CONVERT(decimal(38,4),Debit)) FROM @Input)
      THROW 51004,'Complete original supplier credit cost attribution required.',1;
  END;
  IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind NOT IN(N'Supplier.Payment',N'Supplier.ReverseApplication')
  BEGIN
    IF @SourceCommandKind<>N'Supplier.Apply' OR @SourceKind<>N'SupplierApplication' OR @EventKind<>N'Apply'
      OR @RequiredPermission<>N'SupplierAllocationsManage' OR ISJSON(@SupplierControlItems,ARRAY)<>1
      OR DATALENGTH(@SupplierControlItems)>262144
      OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
      THROW 51004,'Invalid historical supplier control authority.',1;
    EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
    IF (SELECT COUNT(*) FROM OPENJSON(@SupplierControlItems))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(@SupplierControlItems) e WHERE e.type<>5
        OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('ordinal','itemId'))
        OR (SELECT COUNT(DISTINCT [key]) FROM OPENJSON(e.value))<>2)
      THROW 51004,'Invalid historical supplier control references.',1;
    INSERT @Historical
      SELECT line.Ordinal,h.AccountId,h.AccountVersion,h.AccountCode,h.AccountName,h.AccountType,h.AccountPurpose
      FROM OPENJSON(@SupplierControlItems) e
      JOIN Purchasing.SupplierOpenItems item ON item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId'))
      CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) h
      JOIN @Input line ON line.Ordinal=TRY_CONVERT(int,JSON_VALUE(e.value,'$.ordinal'))
        AND line.AccountId=h.AccountId AND line.AccountVersion=h.AccountVersion
      WHERE item.SourcePostingDate<=@PostingDate AND item.Currency=@Currency
        AND ((line.Ordinal=1 AND item.Kind IN('Payable','RefundClearing')
            AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.debtItemId')) AND line.Debit>0 AND line.Credit=0)
          OR (line.Ordinal=2 AND item.Kind IN('Advance','CreditReceivable')
            AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.fundingItemId')) AND line.Credit>0 AND line.Debit=0))
        AND COALESCE(NULLIF(line.Debit,0),line.Credit)=TRY_CONVERT(decimal(28,4),JSON_VALUE(@SourceSnapshot,'$.amount'));
    IF (SELECT COUNT(*) FROM @Historical)<>2 OR (SELECT COUNT(*) FROM @Input)<>2
      THROW 51004,'Historical supplier control evidence is unavailable.',1;
  END;
