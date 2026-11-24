ALTER PROCEDURE Accounting.CorrectJournalCore
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
  @RequiredPermission nvarchar(max),@SourceCommandKind nvarchar(max),@SourceCommandVersion int,
  @CanonicalInput nvarchar(max),@OriginalJournalId uniqueidentifier,
  @ExpectedConfigurationVersion uniqueidentifier,@PostingDate date,
  @Reason nvarchar(max),@Evidence nvarchar(max),
  @ReplacementSourceRevision uniqueidentifier=NULL,@ReplacementDocumentDate date=NULL,
  @ReplacementRuleVersion int=NULL,@ReplacementSnapshot nvarchar(max)=NULL,@ReplacementLines nvarchar(max)=NULL
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
  DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
  IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
    THROW 51003,'Current tenant and actor authority are required.',1;
  IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
    THROW 51000,'A bounded source permission is required.',1;
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
    THROW 51000,'Invalid correction command.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@CanonicalInput) GROUP BY [key] HAVING COUNT(*)>1)
    THROW 51000,'Duplicate command fields are not allowed.',1;
  -- Only the complete group receipt can replay a correction. Internal posting keys are server-owned.
  IF EXISTS(SELECT 1 FROM Accounting.CorrectionReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM Accounting.CorrectionReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
      AND ActorId=@ActorId AND CONVERT(varbinary(max),SourceCommandKind)=CONVERT(varbinary(max),@SourceCommandKind)
      AND SourceCommandVersion=@SourceCommandVersion AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput))
      THROW 51009,'This correction request was already used with different content.',1;
    SELECT g.Id CorrectionId,g.ReversalJournalId,g.ReplacementJournalId,g.ReplacementSourceRevision,g.RecordedAtUtc
      FROM Accounting.CorrectionReceipts r JOIN Accounting.CorrectionGroups g
        ON g.TenantId=r.TenantId AND g.Id=r.CorrectionId
      WHERE r.TenantId=@TenantId AND r.RequestId=@RequestId;
    RETURN;
  END;
  IF @OriginalJournalId IS NULL OR @ExpectedConfigurationVersion IS NULL OR @PostingDate IS NULL
    OR @Reason IS NULL OR DATALENGTH(@Reason)>4000
    OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Reason))=0
    OR @Evidence IS NULL OR DATALENGTH(@Evidence)>262144 OR ISJSON(@Evidence,OBJECT)<>1
    THROW 51000,'Invalid correction evidence or dates.',1;
  DECLARE @OriginalEventId uniqueidentifier,@SourceKind nvarchar(64),@SourceId uniqueidentifier,
    @OriginalRevision uniqueidentifier,@EventKind nvarchar(64),@RuleVersion int,@Snapshot nvarchar(max),@SnapshotHash binary(32),
    @DocumentDate date,@EffectiveDate date,@Currency varchar(3),@Reference nvarchar(200);
  SELECT @OriginalEventId=e.Id,@SourceKind=e.SourceKind,@SourceId=e.SourceId,@OriginalRevision=e.SourceRevision,
    @EventKind=e.EventKind,@RuleVersion=e.RuleVersion,@Snapshot=e.SnapshotJson,@SnapshotHash=e.SnapshotSha256,
    @DocumentDate=j.DocumentDate,@EffectiveDate=j.EffectiveDate,@Currency=j.Currency,@Reference=j.Reference
    FROM Accounting.JournalEntries j WITH(UPDLOCK,HOLDLOCK)
    JOIN Accounting.SourceEvents e ON e.TenantId=j.TenantId AND e.Id=j.SourceEventId
    WHERE j.TenantId=@TenantId AND j.Id=@OriginalJournalId;

  IF @SourceKind=N'PurchaseRecognition' AND (@SourceCommandKind<>N'PurchaseRecognition.CorrectInternal' OR NOT EXISTS(
    SELECT 1 FROM Purchasing.RecognitionEventCorrections c JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=c.TenantId AND e.Id=c.OriginalEventId
    WHERE c.TenantId=@TenantId AND e.JournalId=@OriginalJournalId AND c.AccountingCorrectionGroupId IS NULL))
    THROW 51009,'Purchase recognition requires its complete dependency correction.',1;

  IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents target JOIN Purchasing.RecognitionSideEvents owned
    ON owned.TenantId=target.TenantId AND owned.UnitId=target.UnitId JOIN Purchasing.SupplierBillPostingEvents bill
    ON bill.TenantId=owned.TenantId AND bill.EventId=owned.Id WHERE target.TenantId=@TenantId AND target.JournalId=@OriginalJournalId)
    AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts parent
      JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=parent.TenantId AND financialGroup.Id=parent.GroupId
        AND financialGroup.Operation='CorrectBill' AND financialGroup.RecordedAtUtc=parent.RecordedAtUtc
      JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=parent.TenantId AND correction.GroupId=parent.GroupId
        AND correction.SourceKind='SupplierBill' AND correction.SourceId=financialGroup.SourceId AND correction.ActorId=parent.ActorId
        AND correction.RecordedAtUtc=parent.RecordedAtUtc
      CROSS APPLY OPENJSON(parent.ResultJson,'$.plan.units') unit
      CROSS APPLY OPENJSON(unit.value,'$.originals') original
      JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=parent.TenantId AND actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))
        AND actual.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.unitId')) AND actual.JournalId=@OriginalJournalId
      JOIN Purchasing.RecognitionEventCorrections link ON link.TenantId=actual.TenantId AND link.OriginalEventId=actual.Id
      JOIN Purchasing.RecognitionCorrectionGroups recognitionGroup ON recognitionGroup.TenantId=link.TenantId AND recognitionGroup.Id=link.CorrectionGroupId
        AND recognitionGroup.ActorId=parent.ActorId AND recognitionGroup.RecordedAtUtc=parent.RecordedAtUtc AND recognitionGroup.PostingDate=@PostingDate
      WHERE parent.TenantId=@TenantId AND parent.Operation='CorrectSupplierBill' AND parent.ActorId=@ActorId
        AND JSON_VALUE(parent.ResultJson,'$.state')='pending' AND @RequiredPermission=N'SupplierBillsCorrect'
        AND @SourceCommandKind=N'PurchaseRecognition.CorrectInternal' AND link.AccountingCorrectionGroupId IS NULL
        AND CONVERT(varbinary(max),parent.CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput)
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(parent.CanonicalInput,'$.expectedConfigurationVersion'))=@ExpectedConfigurationVersion
        AND TRY_CONVERT(date,JSON_VALUE(parent.ResultJson,'$.plan.sources[0].postingDate'),23)=@PostingDate)
    THROW 51009,'Bill-owned journals require a complete bill correction adapter.',1;
  IF @OriginalEventId IS NULL THROW 51004,'The original journal is unavailable.',1;
  IF @EventKind=N'Correction.Reversal' OR EXISTS(SELECT 1 FROM Accounting.CorrectionGroups
      WHERE TenantId=@TenantId AND (OriginalJournalId=@OriginalJournalId OR ReversalJournalId=@OriginalJournalId))
    THROW 51009,'The journal is already reversed or is a reversal.',1;
  -- A strict, versioned evidence envelope binds the correction to retained original evidence.
  DECLARE @EvidenceExpected nvarchar(max)=(SELECT 1 schemaVersion,
    CONVERT(nvarchar(36),@OriginalRevision) originalSourceRevision,
    CONVERT(varchar(64),@SnapshotHash,2) originalSnapshotSha256 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  IF (SELECT COUNT(*) FROM OPENJSON(@Evidence))<>3
    OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence) GROUP BY [key] HAVING COUNT(*)>1)
    OR EXISTS(SELECT CONVERT(varbinary(max),[key]),[type],CONVERT(varbinary(max),[value]) FROM OPENJSON(@Evidence)
        EXCEPT SELECT CONVERT(varbinary(max),[key]),[type],CONVERT(varbinary(max),[value]) FROM OPENJSON(@EvidenceExpected))
    THROW 51000,'Invalid versioned correction evidence.',1;
  IF NOT ((@ReplacementSourceRevision IS NULL AND @ReplacementDocumentDate IS NULL AND @ReplacementRuleVersion IS NULL
           AND @ReplacementSnapshot IS NULL AND @ReplacementLines IS NULL)
      OR (@ReplacementSourceRevision IS NOT NULL AND @ReplacementDocumentDate IS NOT NULL AND @ReplacementRuleVersion IS NOT NULL
           AND @ReplacementSnapshot IS NOT NULL AND @ReplacementLines IS NOT NULL))
    THROW 51000,'Replacement fields must be all absent or all present.',1;
  IF @ReplacementSourceRevision IS NOT NULL AND EXISTS(SELECT 1 FROM Accounting.SourceEvents
      WHERE TenantId=@TenantId AND SourceKind=@SourceKind AND SourceId=@SourceId AND SourceRevision=@ReplacementSourceRevision)
    THROW 51009,'A replacement requires a new source revision.',1;
  EXEC Accounting.EnsureOpenPeriod @PostingDate=@PostingDate,@ExpectedConfigurationVersion=@ExpectedConfigurationVersion;
  DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@CorrectionId uniqueidentifier=NEWID(),
    @ReversalEventId uniqueidentifier=NEWID(),@ReversalJournalId uniqueidentifier=NEWID(),
    @ReversalRevision uniqueidentifier=NEWID(),@ReversalRequestId uniqueidentifier=NEWID(),
    @ReplacementEventId uniqueidentifier=NULL,@ReplacementJournalId uniqueidentifier=NULL,@ReplacementRequestId uniqueidentifier=NULL;
  DECLARE @InternalInput nvarchar(max)=(SELECT @CorrectionId correctionId,@OriginalJournalId originalJournalId
    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  -- Narrow inverse append: never accept lines or look up current account descriptions here.
  INSERT Accounting.SourceEvents(Id,TenantId,SourceKind,SourceId,SourceRevision,EventKind,RuleVersion,ActorId,
    DocumentDate,EffectiveDate,PostingDate,Reference,Reason,SnapshotJson,SnapshotSha256,RecordedAtUtc)
    VALUES(@ReversalEventId,@TenantId,@SourceKind,@SourceId,@ReversalRevision,N'Correction.Reversal',@RuleVersion,@ActorId,
      @DocumentDate,@EffectiveDate,@PostingDate,@Reference,@Reason,@Snapshot,
      HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Snapshot)),@Now);
  INSERT Accounting.JournalEntries(Id,TenantId,SourceEventId,ConfigurationVersion,Currency,Scale,
    DocumentDate,EffectiveDate,PostingDate,RecordedAtUtc,ActorId,Reference,Reason,DebitTotal,CreditTotal)
    SELECT @ReversalJournalId,TenantId,@ReversalEventId,ConfigurationVersion,Currency,Scale,
      DocumentDate,EffectiveDate,@PostingDate,@Now,@ActorId,Reference,@Reason,CreditTotal,DebitTotal
    FROM Accounting.JournalEntries WHERE TenantId=@TenantId AND Id=@OriginalJournalId;
  INSERT Accounting.JournalLines(TenantId,JournalId,Ordinal,AccountId,AccountVersion,
    AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit)
    SELECT TenantId,@ReversalJournalId,Ordinal,AccountId,AccountVersion,
      AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit
    FROM Accounting.JournalInverseLines(@TenantId,@OriginalJournalId);
  INSERT Accounting.PostingReceipts(TenantId,RequestId,ActorId,SourceCommandKind,SourceCommandVersion,
    CanonicalInput,InputSha256,SourceEventId,JournalId,Sequence,RecordedAtUtc)
    SELECT @TenantId,@ReversalRequestId,@ActorId,N'Correction.InternalReversal',1,@InternalInput,
      HASHBYTES('SHA2_256',CONVERT(varbinary(max),@InternalInput)),@ReversalEventId,@ReversalJournalId,Sequence,@Now
    FROM Accounting.JournalEntries WHERE TenantId=@TenantId AND Id=@ReversalJournalId;
