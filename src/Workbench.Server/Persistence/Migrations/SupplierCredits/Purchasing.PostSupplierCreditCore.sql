-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostSupplierCreditCore
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max),@OwnerRequestId uniqueidentifier=NULL
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Lock int,
      @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
    IF @Tenant IS NULL THROW 51003,'Current supplier credit authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    DECLARE @Permission nvarchar(100)=CASE WHEN @OwnerRequestId IS NULL THEN N'SupplierCreditsPost' ELSE N'SupplierCreditsCorrect' END;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@Permission; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier credit authority is required.',1; THROW; END CATCH;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid credit request identity.',1;
    EXEC Purchasing.CanonicalizeSupplierCredit N'PostSupplierCredit',@Command,@Canonical OUTPUT;
    DECLARE @ParentGroup uniqueidentifier,@ParentCorrection uniqueidentifier,@ParentNow datetimeoffset,@ParentPlan nvarchar(max);
    DECLARE @ChildCredit uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.creditId'));
    IF @OwnerRequestId IS NOT NULL
      EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N'ReplacementCredit',
        @EntityId=@ChildCredit,@Details=@Canonical,@GroupId=@ParentGroup OUTPUT,@CorrectionId=@ParentCorrection OUTPUT,@RecordedAtUtc=@ParentNow OUTPUT,@Plan=@ParentPlan OUTPUT;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations'))
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
    END;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId
        AND Operation='PostSupplierCredit' AND ActorId=@ActorId AND JSON_VALUE(ResultJson,'$.state') IS NULL
        AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Credit request identity has different or pending content.',1;
      SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId;
      COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson; RETURN;
    END;
    DECLARE @Credit uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.creditId')),
      @Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.creditRevisionId')),
      @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
      @Document date=CONVERT(date,JSON_VALUE(@Canonical,'$.documentDate'),23),@Effective date=CONVERT(date,JSON_VALUE(@Canonical,'$.effectiveDate'),23),
      @Total decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.total')),@Scale int,@Policy nvarchar(max),
      @Reference nvarchar(200)=JSON_VALUE(@Canonical,'$.reference'),@Normalized nvarchar(200),@Group uniqueidentifier=COALESCE(@ParentGroup,NEWID()),@Now datetimeoffset,
      @Reason nvarchar(2000)=JSON_VALUE(@Canonical,'$.reason'),@Receivable uniqueidentifier,@ReceivableVersion uniqueidentifier;
    IF @Document>@Date OR @Effective>@Date THROW 51000,'Credit dates are invalid.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@Tenant AND (Id=@Credit OR RevisionId=@Revision))
      THROW 51009,'Supplier credit source already exists.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@Po AND State='Ordered' AND IsDeleted=0)
      THROW 51004,'Purchase unavailable.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Po
      AND SupplierId=@Supplier AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
      THROW 51009,'Purchase revision changed.',1;
    SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Version=@Config;
    IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
    SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
    IF @Scale NOT BETWEEN 0 AND 4 OR @Scale IS NULL OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR @Total<>ROUND(@Total,@Scale,1) OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.claims') WHERE
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Scale,1))
      THROW 51000,'Credit currency or precision is invalid.',1;
    EXEC Accounting.EnsureOpenPeriod @PostingDate=@Date,@ExpectedConfigurationVersion=@Config;
    SET @Normalized=Purchasing.NormalizeBillReference(@Reference);
    IF COALESCE(LEN(@Normalized),0)=0 THROW 51000,'A supplier credit reference is required.',1;
    IF EXISTS(SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@Tenant AND SupplierId=@Supplier AND NormalizedReference=@Normalized
        EXCEPT SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId')) FROM OPENJSON(@Canonical,'$.duplicateResolutions'))
      OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId')) FROM OPENJSON(@Canonical,'$.duplicateResolutions')
        EXCEPT SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@Tenant AND SupplierId=@Supplier AND NormalizedReference=@Normalized)
      THROW 51009,'Supplier credit reference requires an exact recorded resolution.',1;
    SELECT @Receivable=AccountId,@ReceivableVersion=AccountVersion
      FROM Purchasing.SupplierCorrectionControlAccount(@Tenant,@Policy,'SupplierCreditReceivable','Asset');
    IF @Receivable IS NULL THROW 51004,'Supplier credit account mapping unavailable.',1;
    DECLARE @Derived nvarchar(max),@ClaimInput nvarchar(max)=JSON_QUERY(@Canonical,'$.claims');
    EXEC Purchasing.ValidateSupplierCreditClaims @Tenant,@ClaimInput,@Date,@Derived OUTPUT,@ParentGroup,@Credit;
    DECLARE @Claims TABLE(Id uniqueidentifier PRIMARY KEY,BillId uniqueidentifier,BillRevisionId uniqueidentifier,
      EventId uniqueidentifier,ComponentKey nvarchar(200),Amount decimal(28,4),Treatment varchar(16),AccountId uniqueidentifier,
      AccountVersion uniqueidentifier,JournalId uniqueidentifier,Ordinal int);
    INSERT @Claims SELECT ClaimId,BillId,BillRevisionId,EventId,ComponentKey,Amount,Treatment,AccountId,AccountVersion,JournalId,Ordinal
      FROM OPENJSON(@Derived) WITH(ClaimId uniqueidentifier '$.claimId',BillId uniqueidentifier '$.billId',
        BillRevisionId uniqueidentifier '$.billRevisionId',EventId uniqueidentifier '$.recognitionEventId',ComponentKey nvarchar(200) '$.componentKey',
        Amount decimal(28,4) '$.amount',Treatment varchar(16) '$.treatment',AccountId uniqueidentifier '$.accountId',
        AccountVersion uniqueidentifier '$.accountVersion',JournalId uniqueidentifier '$.journalId',Ordinal int '$.ordinal');
    IF (SELECT COUNT(*) FROM @Claims)<>(SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.claims')) OR EXISTS(
      SELECT 1 FROM @Claims c WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings b
        JOIN Purchasing.SupplierBills owner ON owner.TenantId=b.TenantId AND owner.Id=b.BillId AND owner.State='Posted'
          AND owner.SupplierId=@Supplier AND owner.PurchaseOrderId=@Po AND owner.Currency=@Currency
        JOIN Purchasing.SupplierBillPostingEvents owned ON owned.TenantId=b.TenantId AND owned.BillId=b.BillId AND owned.EventId=c.EventId
        WHERE b.TenantId=@Tenant AND b.BillId=c.BillId AND b.RevisionId=c.BillRevisionId))
      THROW 51004,'Authentic current posted bill components required.',1;
    IF (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Claims)<>@Total
      THROW 51000,'Credit components must exactly equal the total.',1;
    DECLARE @Documents nvarchar(max),@Evidence nvarchar(max)=JSON_QUERY(@Canonical,'$.evidence');
    EXEC Purchasing.ValidateBillEvidence @Tenant,@Po,@Evidence,@Documents OUTPUT;
    DECLARE @Snapshot nvarchar(max)=(SELECT 1 schemaVersion,@Group groupId,@OwnerRequestId correctionRequestId,@ParentCorrection correctionId,JSON_QUERY(@Canonical) command,
      JSON_QUERY((SELECT Id id,Name name FROM Purchasing.Suppliers WHERE TenantId=@Tenant AND Id=@Supplier FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) supplier,
      JSON_QUERY((SELECT Id id,Revision revision,OrderDate orderDate,ContentJson content FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@Po FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) purchase,
      JSON_QUERY((SELECT JSON_QUERY(@Documents) documents,JSON_VALUE(@Evidence,'$.missingEvidenceReason') missingEvidenceReason FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) evidence
      FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @Lines nvarchar(max);
    EXEC Purchasing.BuildSupplierCreditJournalLines @Receivable,@ReceivableVersion,@Total,@Derived,@Lines OUTPUT;
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    SET @Now=COALESCE(@ParentNow,SYSUTCDATETIME());
    IF @OwnerRequestId IS NULL
    INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@Tenant,@Group,'RecordCredit',@Credit,@Now);
    INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@Tenant,@RequestId,@Group,'PostSupplierCredit',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),N'{"state":"pending"}',@Now);
    INSERT Purchasing.SupplierCredits(TenantId,Id,RevisionId,SupplierId,PurchaseOrderId,Currency,DocumentDate,EffectiveDate,PostingDate,Total,Reference,NormalizedReference,Reason,SourceSnapshotJson,ActorId,GroupId,RecordedAtUtc)
      VALUES(@Tenant,@Credit,@Revision,@Supplier,@Po,@Currency,@Document,@Effective,@Date,@Total,@Reference,@Normalized,@Reason,@Snapshot,@ActorId,@Group,@Now);
    INSERT Purchasing.SupplierCreditClaims(TenantId,Id,CreditId,BillId,BillRevisionId,RecognitionEventId,ComponentKey,Treatment,Amount,AccountId,AccountVersion,OriginalJournalId,OriginalJournalOrdinal)
      SELECT @Tenant,Id,@Credit,BillId,BillRevisionId,EventId,ComponentKey,Treatment,Amount,AccountId,AccountVersion,JournalId,Ordinal FROM @Claims;
    INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@RequestId,@Permission,N'Supplier.Credit',1,
      @Canonical,N'SupplierCredit',@Credit,@Revision,N'Credit',1,@Config,@Currency,@Document,@Effective,@Date,@Reference,@Reason,@Snapshot,@Lines;
    UPDATE source SET RecordedAtUtc=@Now FROM Accounting.SourceEvents source JOIN @Posted posted ON posted.SourceEventId=source.Id WHERE source.TenantId=@Tenant;
    UPDATE journal SET RecordedAtUtc=@Now FROM Accounting.JournalEntries journal JOIN @Posted posted ON posted.JournalId=journal.Id WHERE journal.TenantId=@Tenant;
    UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND RequestId=@RequestId;
    UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Posted posted ON posted.JournalId=audit.TargetId
      WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.PostJournal';
    DECLARE @Journal uniqueidentifier=(SELECT JournalId FROM @Posted),@Set uniqueidentifier;
    EXEC Purchasing.AppendSupplierCredit @Tenant,@Credit,@Journal;
    EXEC Accounting.CaptureFinancialEvidence 'SupplierCredit',@Credit,@Revision,@Set OUTPUT;
    DECLARE @ApplicationResult nvarchar(max)=N'{"journalIds":[],"applicationIds":[]}',@ApplicationRequest uniqueidentifier=NEWID();
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations'))
    BEGIN
      DECLARE @Apply nvarchar(max)=(SELECT 1 schemaVersion,N'ApplySupplierFunds' operation,@Config expectedConfigurationVersion,
        @Po purchaseOrderId,JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion') expectedPurchaseOrderVersion,
        @Supplier supplierId,@Currency currency,CONVERT(char(10),@Date,23) postingDate,@Credit fundingItemId,
        CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) expectedFundingItemVersion,
        JSON_QUERY(@Canonical,'$.allocations') targets FROM Purchasing.SupplierItemVersions v
        WHERE v.TenantId=@Tenant AND v.ItemId=@Credit FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),@ApplyCanonical nvarchar(max),@Pending nvarchar(max);
      EXEC Purchasing.ValidateSupplierFinancialCommand N'ApplySupplierFunds',@Apply,@ApplyCanonical OUTPUT;
      SET @Pending=(SELECT N'pending' state,JSON_QUERY((SELECT @ApplicationRequest requestId,N'ApplySupplierFunds' operation,
        @ApplyCanonical canonicalInput FOR JSON PATH)) compositions FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Pending WHERE TenantId=@Tenant AND RequestId=@RequestId;
      EXEC Purchasing.ApplySupplierFundsCore @ActorId,@SessionId,@ApplicationRequest,@ApplyCanonical,@RequestId;
      SELECT @ApplicationResult=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@ApplicationRequest;
    END;
    SET @Result=(SELECT @Group groupId,@Credit sourceId,@Revision sourceRevisionId,
      CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) version,@Now recordedAtUtc,
      JSON_QUERY(N'["'+CONVERT(nvarchar(36),@Journal)+N'"'+COALESCE((SELECT N','+STRING_AGG(CONVERT(nvarchar(max),N'"'+value+N'"'),N',') FROM OPENJSON(@ApplicationResult,'$.journalIds')),N'')+N']') journalIds,
      JSON_QUERY(N'["'+CONVERT(nvarchar(36),@Credit)+N'"]') itemIds,JSON_QUERY(@ApplicationResult,'$.applicationIds') applicationIds,JSON_QUERY(N'[]') settlementIds
      FROM Purchasing.SupplierCreditVersions v WHERE v.TenantId=@Tenant AND v.CreditId=@Credit FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@Tenant AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
    IF @@ROWCOUNT<>1 THROW 51009,'Credit receipt was already finalized.',1;
    IF @OwnerRequestId IS NOT NULL
      INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,ItemId,EffectsJson)
        VALUES(@Tenant,NEWID(),@ParentCorrection,@Group,'Replacement',@Credit,@Journal,@Credit,@Canonical);
    COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
