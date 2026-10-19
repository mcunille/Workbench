-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CorrectSupplierCashCorrection
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max),@Operation nvarchar(40)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),@Lock int,
      @Canonical nvarchar(max),@Plan nvarchar(max),@Result nvarchar(max),@Group uniqueidentifier;
    IF @Tenant IS NULL THROW 51003,'Current cash correction authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid correction request identity.',1;
    EXEC Purchasing.CanonicalizeSupplierSourceCorrection @Operation,@Command,@Canonical OUTPUT;
    EXEC Purchasing.RequireSupplierCashCorrectionAuthority @ActorId,@SessionId,@Operation,@Canonical;
    IF JSON_VALUE(@Canonical,'$.expectedPlanFingerprint') IS NULL THROW 51000,'An approved correction fingerprint is required.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId
        AND Operation=@Operation AND ActorId=@ActorId AND JSON_VALUE(ResultJson,'$.state') IS NULL
        AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Correction request identity has different or pending content.',1;
      SELECT @Group=GroupId,@Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId;
      IF EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers WHERE TenantId=@Tenant AND GroupId=@Group AND (Kind IN('Application','ReverseApplication','ReleaseSettlement') OR JSON_VALUE(EffectsJson,'$.reverseRelease')='true'))
      BEGIN
        BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
        BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
      END;
      COMMIT; SELECT @Result ResultJson; RETURN;
    END;
    EXEC Purchasing.PlanSupplierCashCorrection @Tenant,@ActorId,@SessionId,@Operation,@Canonical,@Plan OUTPUT;
    IF JSON_VALUE(@Plan,'$.planFingerprint') COLLATE Latin1_General_100_BIN2<>JSON_VALUE(@Canonical,'$.expectedPlanFingerprint') COLLATE Latin1_General_100_BIN2
      THROW 51009,'The complete source correction dependencies changed. Preview again.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.applications')) OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reapplications')) OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reverseReleases'))
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
    END;
    DECLARE @Root uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),@Now datetimeoffset=SYSUTCDATETIME(),
      @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Reason nvarchar(2000)=JSON_VALUE(@Canonical,'$.reason'),@Pending nvarchar(max);
    SET @Group=NEWID();
    SET @Pending=(SELECT N'pending' state,JSON_QUERY(@Plan) [plan],JSON_QUERY(N'[]') compositions FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@Tenant,@Group,CASE @Operation WHEN 'CorrectSupplierRefund' THEN 'CorrectRefund' ELSE 'CorrectRepayment' END,@Root,@Now);
    INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@Tenant,@RequestId,@Group,@Operation,@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Pending,@Now);
    INSERT Purchasing.SupplierSourceCorrections(TenantId,Id,GroupId,SourceKind,SourceId,SourceRevisionId,OriginalCashSourceId,
      ReplacementSourceId,ReplacementSourceRevisionId,SupplierId,PurchaseOrderId,Currency,OriginalSourceVersion,PostingDate,Reason,ActorId,RecordedAtUtc)
      SELECT @Tenant,NEWID(),@Group,SourceKind,SourceId,RevisionId,SourceId,ReplacementId,ReplacementRevisionId,@Supplier,@Po,@Currency,
        CONVERT(binary(8),Version,1),PostingDate,@Reason,@ActorId,@Now
      FROM OPENJSON(@Plan,'$.sources') WITH(SourceKind varchar(32) '$.sourceKind',SourceId uniqueidentifier '$.sourceId',RevisionId uniqueidentifier '$.sourceRevisionId',Version varchar(18) '$.version',
        PostingDate date '$.postingDate',ReplacementId uniqueidentifier '$.replacementId',ReplacementRevisionId uniqueidentifier '$.replacementRevisionId');
    DECLARE @Entity uniqueidentifier,@Date date,@Correction uniqueidentifier,@OwnerGroup uniqueidentifier,@OwnerNow datetimeoffset,@OwnerPlan nvarchar(max),
      @Details nvarchar(max),@Journal uniqueidentifier,@Inverse uniqueidentifier,@Events nvarchar(max);
    DECLARE applications CURSOR LOCAL FAST_FORWARD FOR SELECT Id,PostingDate FROM OPENJSON(@Plan,'$.applications')
      WITH(Id uniqueidentifier '$.applicationId',PostingDate date '$.postingDate') ORDER BY PostingDate,Id;
    OPEN applications; FETCH NEXT FROM applications INTO @Entity,@Date;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @Details=(SELECT @Date postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@RequestId,N'ReverseApplication',@Entity,@Details,
        @OwnerGroup OUTPUT,@Correction OUTPUT,@OwnerNow OUTPUT,@OwnerPlan OUTPUT;
      EXEC Purchasing.PostSupplierApplicationInverse @ActorId,@SessionId,@Entity,@Group,@Now,@Config,@Date,@Reason,@Inverse OUTPUT,@Journal OUTPUT;
      SET @Events=(SELECT @Inverse reversalId,@Journal journalId FOR JSON PATH);
      EXEC Purchasing.AppendSupplierEventGroup @Tenant,@Group,@Now,@Events;
      SET @Details=(SELECT @Inverse reversalId,@Entity applicationId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,EffectsJson)
        VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseApplication',@Entity,@Journal,@Details);
      FETCH NEXT FROM applications INTO @Entity,@Date;
    END;
    CLOSE applications; DEALLOCATE applications;
    -- Restore all whole cash sources before cancelling any released clearing opening.
    DECLARE sources CURSOR LOCAL FAST_FORWARD FOR SELECT Id,PostingDate FROM OPENJSON(@Plan,'$.sources')
      WITH(Id uniqueidentifier '$.sourceId',PostingDate date '$.postingDate') ORDER BY PostingDate,Id;
    OPEN sources; FETCH NEXT FROM sources INTO @Entity,@Date;
    WHILE @@FETCH_STATUS=0
    BEGIN
      EXEC Purchasing.ReverseSupplierCashSource @ActorId,@SessionId,@RequestId,@Entity,@Date;
      FETCH NEXT FROM sources INTO @Entity,@Date;
    END;
    CLOSE sources; DEALLOCATE sources;
    DECLARE releases CURSOR LOCAL FAST_FORWARD FOR SELECT Id,PostingDate FROM OPENJSON(@Plan,'$.reverseReleases')
      WITH(Id uniqueidentifier '$.journalId',PostingDate date '$.postingDate') ORDER BY PostingDate,Id;
    OPEN releases; FETCH NEXT FROM releases INTO @Entity,@Date;
    WHILE @@FETCH_STATUS=0
    BEGIN
      EXEC Purchasing.ReverseSupplierCashSource @ActorId,@SessionId,@RequestId,@Entity,@Date,1;
      FETCH NEXT FROM releases INTO @Entity,@Date;
    END;
    CLOSE releases; DEALLOCATE releases;
    DECLARE @Replacement nvarchar(max),@ChildRequest uniqueidentifier,@Composition nvarchar(max),@CashKind varchar(32),@ChildResult nvarchar(max),@ChildOperation nvarchar(40);
    DECLARE replacements CURSOR LOCAL FAST_FORWARD FOR SELECT SourceKind,Replacement FROM OPENJSON(@Plan,'$.sources')
      WITH(SourceKind varchar(32) '$.sourceKind',Replacement nvarchar(max) '$.replacement' AS JSON) WHERE Replacement IS NOT NULL;
    OPEN replacements; FETCH NEXT FROM replacements INTO @CashKind,@Replacement;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @ChildRequest=NEWID();
      SET @ChildOperation=CASE @CashKind WHEN 'SupplierRefund' THEN N'RecordSupplierRefund' ELSE N'RecordSupplierRefundRepayment' END;
      SET @Composition=(SELECT @ChildRequest requestId,@ChildOperation operation,@Replacement canonicalInput FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=JSON_MODIFY(ResultJson,'append $.compositions',JSON_QUERY(@Composition)) WHERE TenantId=@Tenant AND RequestId=@RequestId;
      EXEC Purchasing.RecordSupplierCashCore @ActorId,@SessionId,@ChildRequest,@Replacement,@CashKind,@ChildResult OUTPUT,1,@RequestId;
      FETCH NEXT FROM replacements INTO @CashKind,@Replacement;
    END;
    CLOSE replacements; DEALLOCATE replacements;
    DECLARE @Funding uniqueidentifier,@Debt uniqueidentifier,@Amount decimal(28,4);
    DECLARE reapplications CURSOR LOCAL FAST_FORWARD FOR SELECT Funding,Debt,Amount,PostingDate FROM OPENJSON(@Plan,'$.reapplications')
      WITH(Funding uniqueidentifier '$.fundingItemId',Debt uniqueidentifier '$.debtItemId',Amount decimal(28,4) '$.amount',PostingDate date '$.postingDate')
      ORDER BY PostingDate,Funding,Debt;
    OPEN reapplications; FETCH NEXT FROM reapplications INTO @Funding,@Debt,@Amount,@Date;
    WHILE @@FETCH_STATUS=0
    BEGIN
      EXEC Purchasing.ApplySupplierClearingFunds @ActorId,@SessionId,@RequestId,@Date,@Funding,@Debt,@Amount;
      FETCH NEXT FROM reapplications INTO @Funding,@Debt,@Amount,@Date;
    END;
    CLOSE reapplications; DEALLOCATE reapplications;
    EXEC Purchasing.ValidateSupplierSourceCorrectionGroup @Tenant,@Group,@RequestId;
    UPDATE version SET CashSourceId=version.CashSourceId FROM Purchasing.SupplierCashVersions version
      JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=version.TenantId AND correction.SourceId=version.CashSourceId
        AND correction.SourceKind IN('SupplierRefund','SupplierRefundRepayment') AND correction.GroupId=@Group AND correction.OriginalSourceVersion=version.RowVersion
      WHERE version.TenantId=@Tenant;
    IF @@ROWCOUNT<>(SELECT COUNT(*) FROM OPENJSON(@Plan,'$.sources')) THROW 51009,'A source coordination version changed.',1;
    DECLARE @RootCorrection uniqueidentifier=(SELECT Id FROM Purchasing.SupplierSourceCorrections WHERE TenantId=@Tenant AND GroupId=@Group AND SourceId=@Root AND SourceKind IN('SupplierRefund','SupplierRefundRepayment'));
    SET @Result=(SELECT @Group groupId,@Root sourceId,source.RevisionId sourceRevisionId,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1) version,@RootCorrection correctionId,@Now recordedAtUtc,
      JSON_QUERY((SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),JournalId)+N'"'),N','),N'')+N']'
        FROM(SELECT DISTINCT JournalId FROM Purchasing.SupplierSourceCorrectionMembers WHERE TenantId=@Tenant AND GroupId=@Group AND JournalId IS NOT NULL) journals)) journalIds,
      JSON_QUERY((SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N','),N'')+N']' FROM Purchasing.SupplierOpenItems item
        WHERE item.TenantId=@Tenant AND EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements opening WHERE opening.TenantId=item.TenantId AND opening.ItemId=item.Id AND opening.GroupId=@Group AND opening.EventKind='Open'))) itemIds,
      JSON_QUERY((SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N','),N'')+N']'
        FROM Purchasing.SupplierApplications WHERE TenantId=@Tenant AND GroupId=@Group)) applicationIds,
      JSON_QUERY((SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),settlement.Id)+N'"'),N','),N'')+N']'
        FROM Purchasing.SupplierCashSettlements settlement JOIN Purchasing.SupplierCashSources cash
          ON cash.TenantId=settlement.TenantId AND cash.Id=settlement.CashSourceId
        WHERE cash.TenantId=@Tenant AND cash.GroupId=@Group)) settlementIds
      FROM Purchasing.SupplierCashSources source JOIN Purchasing.SupplierCashVersions version ON version.TenantId=source.TenantId AND version.CashSourceId=source.Id
      WHERE source.TenantId=@Tenant AND source.Id=@Root FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@Tenant AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
    IF @@ROWCOUNT<>1 THROW 51009,'Correction receipt was already finalized.',1;
    INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
      VALUES(NEWID(),@Tenant,@ActorId,N'Purchasing.'+@Operation,N'SupplierSourceCorrection',@RootCorrection,@Now);
    COMMIT; SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
