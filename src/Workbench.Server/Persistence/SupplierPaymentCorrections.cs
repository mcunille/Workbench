// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierPaymentCorrections
{

    internal const string CompositionSql = """
        CREATE PROCEDURE Purchasing.RequireSupplierComposition
          @ActorId uniqueidentifier,@OwnerRequestId uniqueidentifier,@RequestId uniqueidentifier,@Operation nvarchar(40),
          @Canonical nvarchar(max),@GroupId uniqueidentifier OUTPUT,@RecordedAtUtc datetimeoffset OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @@TRANCOUNT=0 OR @TenantId IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Supplier composition requires its owner transaction.',1;
          SELECT @GroupId=r.GroupId,@RecordedAtUtc=r.RecordedAtUtc
            FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=r.TenantId AND g.Id=r.GroupId
            CROSS APPLY OPENJSON(r.ResultJson,'$.compositions') WITH(RequestId uniqueidentifier '$.requestId',Operation nvarchar(40) '$.operation',Canonical nvarchar(max) '$.canonicalInput') c
            WHERE r.TenantId=@TenantId AND r.RequestId=@OwnerRequestId AND r.ActorId=@ActorId
              AND JSON_VALUE(r.ResultJson,'$.state')='pending' AND c.RequestId=@RequestId AND c.Operation=@Operation
              AND CONVERT(varbinary(max),c.Canonical)=CONVERT(varbinary(max),@Canonical) AND g.RecordedAtUtc=r.RecordedAtUtc
              AND ((@Operation='RecordSupplierPayment' AND r.Operation='CorrectSupplierPayment' AND g.Operation='CorrectPayment'
                  AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.paymentId'))
                  AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.replacement.paymentId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')))
                OR (@Operation='ApplySupplierFunds' AND r.Operation='ReverseSupplierApplication' AND g.Operation='ReverseApplication'
                  AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.applicationId'))));
          IF @GroupId IS NULL OR @RecordedAtUtc IS NULL THROW 51004,'Authentic pending supplier composition is unavailable.',1;
        END;
        """;

    internal const string CompositionKernelSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.PostJournal')),
          @Anchor nvarchar(max)=N'IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind=N''Supplier.Payment''';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0
          OR CHARINDEX(N'@RequiredPermission<>N''SupplierPaymentsRecord''',@Definition)=0
          THROW 50020,'Unsupported supplier composition journal predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,@Anchor,N'
          IF @SourceCommandKind=N''Supplier.Payment'' AND @RequiredPermission=N''SupplierPaymentsCorrect''
          BEGIN
            DECLARE @CompositionOwner uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.correctionRequestId'')),
              @CompositionGroup uniqueidentifier,@CompositionInstant datetimeoffset,@CompositionRequest uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.correctionCommandRequestId''));
            IF @CompositionOwner IS NULL THROW 51004,''Payment replacement requires its pending correction owner.'',1;
            EXEC Purchasing.RequireSupplierComposition @ActorId,@CompositionOwner,@CompositionRequest,N''RecordSupplierPayment'',@CanonicalInput,
              @CompositionGroup OUTPUT,@CompositionInstant OUTPUT;
            IF @CompositionGroup<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.groupId''))
              THROW 51004,''Payment replacement belongs to a different correction.'',1;
          END;
          '+@Anchor);
        SET @Definition=REPLACE(@Definition,N'@RequiredPermission<>N''SupplierPaymentsRecord''',
          N'@RequiredPermission NOT IN(N''SupplierPaymentsRecord'',N''SupplierPaymentsCorrect'')');
        SET @Definition=REPLACE(@Definition,N'EXEC Accounting.RequirePermission @ActorId,@SessionId,N''SupplierPaymentsRecord'';',
          N'EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;');
        EXEC sys.sp_executesql @Definition;
        """;

    // Install the existing inverse implementation once, behind an internal core.
    // All predecessor recognition/bill guards remain in that body unchanged.
    internal const string KernelSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.CorrectJournal'));
        IF @Definition IS NULL OR CHARINDEX(N'IF @OriginalEventId IS NULL',@Definition)=0
          OR CHARINDEX(N'Bill-owned journals require a complete bill correction adapter.',@Definition)=0
          OR CHARINDEX(N'Purchase recognition requires its complete dependency correction.',@Definition)=0
          THROW 50020,'Unsupported supplier correction kernel predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'ALTER PROCEDURE Accounting.CorrectJournal',N'CREATE PROCEDURE Accounting.CorrectJournalCore');
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE Accounting.CorrectJournal'+NCHAR(13),N'CREATE PROCEDURE Accounting.CorrectJournalCore'+NCHAR(13));
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE Accounting.CorrectJournal'+NCHAR(10),N'CREATE PROCEDURE Accounting.CorrectJournalCore'+NCHAR(10));
        IF CHARINDEX(N'CREATE PROCEDURE Accounting.CorrectJournalCore',@Definition)=0
          THROW 50020,'Supplier correction core hook did not match.',1;
        EXEC sys.sp_executesql @Definition;
        DENY EXECUTE ON Accounting.CorrectJournalCore TO workbench_web;
        DENY EXECUTE ON Accounting.CorrectJournalCore TO workbench_worker;
        """;

    internal const string GenericGuardSql = """
        ALTER PROCEDURE Accounting.CorrectJournal
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @RequiredPermission nvarchar(max),@SourceCommandKind nvarchar(max),@SourceCommandVersion int,
          @CanonicalInput nvarchar(max),@OriginalJournalId uniqueidentifier,
          @ExpectedConfigurationVersion uniqueidentifier,@PostingDate date,
          @Reason nvarchar(max),@Evidence nvarchar(max),
          @ReplacementSourceRevision uniqueidentifier=NULL,@ReplacementDocumentDate date=NULL,
          @ReplacementRuleVersion int=NULL,@ReplacementSnapshot nvarchar(max)=NULL,@ReplacementLines nvarchar(max)=NULL
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
            @LockResult int,@Resource nvarchar(255);
          IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
            THROW 51003,'Current tenant and actor authority are required.',1;
          IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
            THROW 51000,'A bounded source permission is required.',1;
          SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
          BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission; END TRY
          BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1; THROW; END CATCH;
          IF EXISTS(SELECT 1 FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s
              ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId WHERE j.TenantId=@TenantId AND j.Id=@OriginalJournalId
              AND (s.SourceKind IN('SupplierPayment','SupplierApplication','SupplierApplicationReversal')
                OR EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=s.TenantId AND m.SourceEventId=s.Id
                  AND m.RecognitionEventId IS NULL)))
            THROW 51009,'Supplier journals require their complete source-owner correction.',1;
          EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@RequestId,@RequiredPermission,@SourceCommandKind,@SourceCommandVersion,
            @CanonicalInput,@OriginalJournalId,@ExpectedConfigurationVersion,@PostingDate,@Reason,@Evidence,
            @ReplacementSourceRevision,@ReplacementDocumentDate,@ReplacementRuleVersion,@ReplacementSnapshot,@ReplacementLines;
        END;
        """;
    internal const string PlanSql = """
        CREATE PROCEDURE Purchasing.PlanSupplierCorrection
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
            DECLARE @PaymentInput nvarchar(max)=@ReplacementCanonical;
        """ + SupplierPaymentCommands.InputValidationSql + """
            DECLARE @EvidenceInput nvarchar(max)=JSON_QUERY(@ReplacementCanonical,'$.evidence'),@ReplacementDocuments nvarchar(max);
        """ + SupplierPaymentCommands.EvidenceValidationSql + """
            EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@EvidenceInput,@ReplacementDocuments OUTPUT;
            IF (SELECT COUNT(*) FROM OPENJSON(@ReplacementCanonical))<>20
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
        """;
    internal const string PreviewSql = """
        CREATE PROCEDURE Purchasing.PreviewSupplierPaymentCorrection
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255),@Plan nvarchar(max),@Canonical nvarchar(max);
            IF @TenantId IS NULL THROW 51003,'Current supplier payment correction authority is required.',1;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierPaymentsCorrect';
              EXEC Purchasing.ValidateSupplierFinancialCommand N'CorrectSupplierPayment',@Command,@Canonical OUTPUT;
              IF EXISTS(SELECT 1 FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND FundingItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')))
                OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.replacement.allocations'))
                EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
            END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier correction authority is required.',1; THROW; END CATCH;
            EXEC Purchasing.PlanSupplierCorrection @TenantId,@Canonical,@Plan OUTPUT;
            COMMIT; SELECT @Plan ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
    internal const string EventsSql = """
          IF JSON_VALUE(@Events,'$[0].paymentInverseJournalId') IS NOT NULL
          BEGIN
            IF @Count<>1 OR (SELECT COUNT(*) FROM OPENJSON(JSON_QUERY(@Events,'$[0]')))<>2
              OR EXISTS(SELECT 1 FROM OPENJSON(JSON_QUERY(@Events,'$[0]')) WHERE [key] NOT IN('paymentInverseJournalId','compensations'))
              THROW 51000,'Invalid payment inverse identity.',1;
            DECLARE @CorrectionJournal uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].paymentInverseJournalId')),
              @OriginalPayment uniqueidentifier,@OriginalPaymentSource uniqueidentifier,@OriginalPaymentJournal uniqueidentifier,
              @PaymentInverseSource uniqueidentifier,@PaymentInverseDate date,@PaymentInverseActor uniqueidentifier,@PaymentInverseReason nvarchar(2000);
            SELECT @OriginalPayment=p.Id,@OriginalPaymentSource=c.OriginalSourceEventId,@OriginalPaymentJournal=c.OriginalJournalId,
              @PaymentInverseSource=c.ReversalSourceEventId,@PaymentInverseDate=c.PostingDate,@PaymentInverseActor=c.ActorId,@PaymentInverseReason=c.Reason
              FROM Accounting.CorrectionGroups c JOIN Accounting.SourceEvents s ON s.TenantId=c.TenantId AND s.Id=c.OriginalSourceEventId
              JOIN Purchasing.SupplierPayments p ON p.TenantId=s.TenantId AND p.Id=s.SourceId AND p.RevisionId=s.SourceRevision AND s.SourceKind='SupplierPayment' AND s.EventKind='Payment'
              JOIN Purchasing.SupplierFinancialReceipts r ON r.TenantId=c.TenantId AND r.GroupId=@GroupId AND r.ActorId=c.ActorId
                AND r.Operation='CorrectSupplierPayment' AND JSON_VALUE(r.ResultJson,'$.state')='pending'
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.paymentId'))=p.Id
              JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=r.TenantId AND g.Id=r.GroupId AND g.Operation='CorrectPayment' AND g.SourceId=p.Id AND g.RecordedAtUtc=@RecordedAtUtc
              WHERE c.TenantId=@TenantId AND c.ReversalJournalId=@CorrectionJournal AND c.RecordedAtUtc=@RecordedAtUtc;
            IF @OriginalPayment IS NULL OR (SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl(@TenantId,@OriginalPayment))<>1
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections WHERE TenantId=@TenantId AND OriginalPaymentId=@OriginalPayment)
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
              THROW 51004,'Authentic complete payment inverse is unavailable.',1;
            IF EXISTS(SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Credit,Debit
                FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@OriginalPaymentJournal EXCEPT
                SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit
                FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
              OR (SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@OriginalPaymentJournal)
                <>(SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
              THROW 51004,'Payment inverse changed historical journal evidence.',1;
            DECLARE @PaymentInverseEffects TABLE(Id uniqueidentifier PRIMARY KEY,OriginalMovementId uniqueidentifier,ItemId uniqueidentifier,
              EventKind varchar(24),Amount decimal(28,4),SourceEventId uniqueidentifier,JournalId uniqueidentifier,OriginalJournalId uniqueidentifier);
            INSERT @PaymentInverseEffects SELECT NEWID(),m.Id,m.ItemId,CASE m.EventKind WHEN 'Open' THEN 'ReverseSource' ELSE 'ReverseApplication' END,-m.Amount,
              @PaymentInverseSource,@CorrectionJournal,@OriginalPaymentJournal
              FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=@TenantId AND m.SourceEventId=@OriginalPaymentSource;
            DECLARE @Compensations TABLE(ApplicationId uniqueidentifier PRIMARY KEY,OriginalSource uniqueidentifier,OriginalJournal uniqueidentifier,
              InverseSource uniqueidentifier,InverseJournal uniqueidentifier UNIQUE);
            INSERT @Compensations SELECT a.Id,old.Id,oldJournal.Id,c.ReversalSourceEventId,c.ReversalJournalId
              FROM OPENJSON(@Events,'$[0].compensations') e
              JOIN Purchasing.SupplierApplicationReversals r ON r.TenantId=@TenantId AND r.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.reversalId'))
              JOIN Purchasing.SupplierApplications a ON a.TenantId=r.TenantId AND a.Id=r.ApplicationId AND a.FundingItemId=@OriginalPayment
              JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
              JOIN Accounting.SourceEvents old ON old.TenantId=r.TenantId AND old.SourceKind='SupplierApplicationReversal' AND old.SourceId=r.Id
                AND old.SourceRevision=r.GroupId AND old.EventKind='Reverse' AND old.PostingDate=r.PostingDate AND old.RecordedAtUtc=r.RecordedAtUtc AND old.ActorId=r.ActorId
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(old.SnapshotJson,'$.applicationId'))=a.Id
              JOIN Accounting.JournalEntries oldJournal ON oldJournal.TenantId=old.TenantId AND oldJournal.SourceEventId=old.Id
              JOIN Accounting.CorrectionGroups c ON c.TenantId=old.TenantId AND c.OriginalSourceEventId=old.Id AND c.OriginalJournalId=oldJournal.Id
                AND c.ReversalJournalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId')) AND c.RecordedAtUtc=@RecordedAtUtc
                AND c.ActorId=@PaymentInverseActor AND c.PostingDate=@PaymentInverseDate
              JOIN Accounting.CorrectionReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.CorrectionId=c.Id AND receipt.SourceCommandKind='Supplier.CorrectPayment'
              JOIN Purchasing.SupplierFinancialReceipts owner ON owner.TenantId=c.TenantId AND owner.GroupId=@GroupId AND owner.Operation='CorrectSupplierPayment'
                AND JSON_VALUE(owner.ResultJson,'$.state')='pending' AND CONVERT(varbinary(max),owner.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput);
            IF (SELECT COUNT(*) FROM @Compensations)<>(SELECT COUNT(*) FROM OPENJSON(@Events,'$[0].compensations'))
              OR (SELECT COUNT(*) FROM @Compensations)<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications a
                JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
                JOIN Purchasing.SupplierApplicationReversals r ON r.TenantId=a.TenantId AND r.ApplicationId=a.Id WHERE p.TenantId=@TenantId AND p.Id=@OriginalPayment)
              OR EXISTS(SELECT 1 FROM @Compensations c CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,c.ApplicationId) a WHERE
                (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource)<>2
                OR (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=c.OriginalJournal)<>2
                OR (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=c.InverseJournal)<>2
                OR EXISTS(SELECT 1 FROM (VALUES(a.DebtItemId,a.DebtAccountId,a.DebtAccountVersion,1),(a.FundingItemId,a.FundingAccountId,a.FundingAccountVersion,2)) x(ItemId,AccountId,Version,Ordinal)
                  WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m JOIN Purchasing.SupplierControlAttributions t ON t.TenantId=m.TenantId AND t.MovementId=m.Id
                    JOIN Accounting.JournalLines l ON l.TenantId=t.TenantId AND l.JournalId=t.JournalId AND l.Ordinal=t.Ordinal
                    WHERE m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource AND m.ItemId=x.ItemId AND m.EventKind='ReverseApplication' AND m.Amount=a.Amount
                      AND t.JournalId=c.OriginalJournal AND t.Ordinal=x.Ordinal AND t.AccountId=x.AccountId AND t.AccountVersion=x.Version AND t.Amount=m.Amount
                      AND l.AccountId=t.AccountId AND l.AccountVersion=t.AccountVersion AND l.AccountPurpose=t.AccountPurpose
                      AND ((x.Ordinal=1 AND l.Credit=a.Amount AND l.Debit=0) OR (x.Ordinal=2 AND l.Debit=a.Amount AND l.Credit=0)))<>1)
                OR EXISTS(SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Credit,Debit
                  FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=c.OriginalJournal EXCEPT
                  SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit
                  FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=c.InverseJournal))
              THROW 51004,'Embedded unapplication compensation is incomplete.',1;
            INSERT @PaymentInverseEffects SELECT NEWID(),m.Id,m.ItemId,'ReverseSource',-m.Amount,c.InverseSource,c.InverseJournal,c.OriginalJournal
              FROM @Compensations c JOIN Purchasing.SupplierItemMovements m ON m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource;
            DECLARE @PaymentInverseMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,@PaymentInverseDate postingDate,
              CONVERT(nvarchar(60),Amount) amount FROM @PaymentInverseEffects FOR JSON PATH);
            EXEC Purchasing.AssertSupplierAvailability @TenantId,@PaymentInverseMovements;
            INSERT Purchasing.SupplierPaymentCorrections(TenantId,Id,OriginalPaymentId,GroupId,Reason,ActorId,RecordedAtUtc)
              VALUES(@TenantId,@GroupId,@OriginalPayment,@GroupId,@PaymentInverseReason,@PaymentInverseActor,@RecordedAtUtc);
            INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
              SELECT @TenantId,NEWID(),@GroupId,a.Id,@PaymentInverseDate,@PaymentInverseReason,@PaymentInverseActor,@RecordedAtUtc
              FROM Purchasing.SupplierApplications a JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
              WHERE a.TenantId=@TenantId AND p.Id=@OriginalPayment
                AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=a.TenantId AND r.ApplicationId=a.Id);
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              SELECT @TenantId,Id,ItemId,@GroupId,EventKind,SourceEventId,@PaymentInverseDate,Amount,@RecordedAtUtc FROM @PaymentInverseEffects;
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @TenantId,NEWID(),@GroupId,e.Id,e.JournalId,a.Ordinal,a.AccountId,a.AccountVersion,a.AccountPurpose,-a.Amount
              FROM @PaymentInverseEffects e JOIN Purchasing.SupplierControlAttributions a ON a.TenantId=@TenantId AND a.MovementId=e.OriginalMovementId
                AND a.JournalId=e.OriginalJournalId;
            UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v WHERE v.TenantId=@TenantId
              AND EXISTS(SELECT 1 FROM @PaymentInverseEffects e WHERE e.ItemId=v.ItemId);
            UPDATE v SET ApplicationId=v.ApplicationId FROM Purchasing.SupplierApplicationVersions v WHERE v.TenantId=@TenantId
              AND EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=@TenantId AND r.GroupId=@GroupId AND r.ApplicationId=v.ApplicationId);
            UPDATE Purchasing.SupplierPaymentVersions SET PaymentId=PaymentId WHERE TenantId=@TenantId AND PaymentId=@OriginalPayment;
            RETURN;
          END;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.CorrectSupplierPayment
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255),@Plan nvarchar(max),@Canonical nvarchar(max),@Result nvarchar(max);
            IF @TenantId IS NULL THROW 51003,'Current supplier correction authority is required.',1;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierPaymentsCorrect';
              EXEC Purchasing.ValidateSupplierFinancialCommand N'CorrectSupplierPayment',@Command,@Canonical OUTPUT;
              IF EXISTS(SELECT 1 FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND FundingItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')))
                OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.replacement.allocations'))
                EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
            END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier correction authority is required.',1; THROW; END CATCH;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                AND Operation='CorrectSupplierPayment' AND ActorId=@ActorId
                AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)
                AND COALESCE(JSON_VALUE(ResultJson,'$.state'),'complete')<>'pending')
                THROW 51009,'Request identity has different or pending content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            EXEC Purchasing.PlanSupplierCorrection @TenantId,@Canonical,@Plan OUTPUT;
            IF JSON_VALUE(@Canonical,'$.expectedPlanFingerprint') IS NULL
              OR CONVERT(varbinary(max),JSON_VALUE(@Canonical,'$.expectedPlanFingerprint'))<>CONVERT(varbinary(max),JSON_VALUE(@Plan,'$.fingerprint'))
              THROW 51009,'Supplier correction preview changed.',1;
            DECLARE @Payment uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.paymentId')),
              @OriginalJournal uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.journalId')),
              @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
              @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Reason nvarchar(max)=JSON_VALUE(@Canonical,'$.reason'),
              @Group uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME(),@KernelRequest uniqueidentifier=NEWID(),
              @Evidence nvarchar(max),@Journal uniqueidentifier,@Reversal uniqueidentifier,@Application uniqueidentifier;
            -- The complete source-owner receipt is pending only inside this locked transaction.
            INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@TenantId,@Group,'CorrectPayment',@Payment,@Now);
            SET @Result=(SELECT 'pending' state,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@RequestId,@Group,'CorrectSupplierPayment',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
            DECLARE applications CURSOR LOCAL FAST_FORWARD FOR SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.id'))
              FROM OPENJSON(@Plan,'$.applications') WHERE JSON_VALUE(value,'$.embedded')='false' AND JSON_VALUE(value,'$.reversalId') IS NULL ORDER BY JSON_VALUE(value,'$.id');
            OPEN applications; FETCH NEXT FROM applications INTO @Application;
            WHILE @@FETCH_STATUS=0
            BEGIN
              EXEC Purchasing.PostSupplierApplicationInverse @ActorId,@SessionId,@Application,@Group,@Now,@Config,@Date,@Reason,@Reversal OUTPUT,@Journal OUTPUT;
              DECLARE @ApplicationEvents nvarchar(max)=(SELECT @Reversal reversalId,@Journal journalId FOR JSON PATH);
              EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@ApplicationEvents;
              FETCH NEXT FROM applications INTO @Application;
            END;
            CLOSE applications; DEALLOCATE applications;
            DECLARE @Corrected TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,
              ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
            DECLARE @InverseJournals TABLE(OriginalJournalId uniqueidentifier PRIMARY KEY,ReversalId uniqueidentifier,JournalId uniqueidentifier);
            INSERT @InverseJournals VALUES(@OriginalJournal,NULL,NULL);
            INSERT @InverseJournals SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.reversalJournalId')),
              CONVERT(uniqueidentifier,JSON_VALUE(value,'$.reversalId')),NULL FROM OPENJSON(@Plan,'$.applications')
              WHERE JSON_VALUE(value,'$.embedded')='true' AND JSON_VALUE(value,'$.reversalId') IS NOT NULL;
            DECLARE @OriginalInverseJournal uniqueidentifier;
            DECLARE inverses CURSOR LOCAL FAST_FORWARD FOR SELECT OriginalJournalId FROM @InverseJournals ORDER BY OriginalJournalId;
            OPEN inverses; FETCH NEXT FROM inverses INTO @OriginalInverseJournal;
            WHILE @@FETCH_STATUS=0
            BEGIN
              SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),s.SourceRevision) originalSourceRevision,
                CONVERT(varchar(64),s.SnapshotSha256,2) originalSnapshotSha256 FROM Accounting.SourceEvents s
                JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id
                WHERE j.TenantId=@TenantId AND j.Id=@OriginalInverseJournal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              DELETE @Corrected; SET @KernelRequest=NEWID();
              INSERT @Corrected EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@KernelRequest,N'SupplierPaymentsCorrect',N'Supplier.CorrectPayment',1,
                @Canonical,@OriginalInverseJournal,@Config,@Date,@Reason,@Evidence;
              SELECT @Journal=ReversalJournalId FROM @Corrected;
              UPDATE @InverseJournals SET JournalId=@Journal WHERE OriginalJournalId=@OriginalInverseJournal;
              UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id=(SELECT SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@TenantId AND Id=@Journal);
              UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id=@Journal;
              UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND JournalId=@Journal;
              UPDATE c SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups c JOIN @Corrected r ON r.CorrectionId=c.Id WHERE c.TenantId=@TenantId;
              UPDATE a SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents a JOIN @Corrected r ON r.CorrectionId=a.TargetId
                WHERE a.TenantId=@TenantId AND a.Action='Accounting.CorrectJournal';
              FETCH NEXT FROM inverses INTO @OriginalInverseJournal;
            END;
            CLOSE inverses; DEALLOCATE inverses;
            SELECT @Journal=JournalId FROM @InverseJournals WHERE OriginalJournalId=@OriginalJournal;
            DECLARE @Events nvarchar(max)=(SELECT @Journal paymentInverseJournalId,
              JSON_QUERY(COALESCE((SELECT ReversalId reversalId,JournalId journalId FROM @InverseJournals WHERE ReversalId IS NOT NULL ORDER BY ReversalId FOR JSON PATH),N'[]')) compensations FOR JSON PATH);
            EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@Events;
            DECLARE @Replacement nvarchar(max)=JSON_QUERY(@Canonical,'$.replacement'),@ReplacementCanonical nvarchar(max),
              @ReplacementId uniqueidentifier,@NestedRequest uniqueidentifier=NEWID(),@TargetPosition int,@TargetVersion varchar(18),@TargetPath nvarchar(100);
            IF @Replacement IS NOT NULL
            BEGIN
              -- Preview checked caller versions. The inverse has now advanced those same locked coordination rows.
              DECLARE targets CURSOR LOCAL FAST_FORWARD FOR SELECT CONVERT(int,e.[key]),CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1)
                FROM OPENJSON(@Replacement,'$.allocations') e JOIN Purchasing.SupplierItemVersions v ON v.TenantId=@TenantId
                  AND v.ItemId=CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId')) ORDER BY CONVERT(int,e.[key]);
              OPEN targets; FETCH NEXT FROM targets INTO @TargetPosition,@TargetVersion;
              WHILE @@FETCH_STATUS=0
              BEGIN
                SET @TargetPath=N'$.allocations['+CONVERT(nvarchar(10),@TargetPosition)+N'].expectedItemVersion';
                SET @Replacement=JSON_MODIFY(@Replacement,@TargetPath,@TargetVersion);
                FETCH NEXT FROM targets INTO @TargetPosition,@TargetVersion;
              END;
              CLOSE targets; DEALLOCATE targets;
              EXEC Purchasing.ValidateSupplierFinancialCommand N'RecordSupplierPayment',@Replacement,@ReplacementCanonical OUTPUT;
              DECLARE @Compositions nvarchar(max)=(SELECT @NestedRequest requestId,'RecordSupplierPayment' operation,@ReplacementCanonical canonicalInput FOR JSON PATH);
              UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=JSON_MODIFY(ResultJson,'$.compositions',JSON_QUERY(@Compositions))
                WHERE TenantId=@TenantId AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
              IF @@ROWCOUNT<>1 THROW 51009,'Supplier correction receipt was already finalized.',1;
              EXEC Purchasing.RecordSupplierPaymentCore @ActorId,@SessionId,@NestedRequest,@ReplacementCanonical,@RequestId;
              SET @ReplacementId=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.paymentId'));
              UPDATE Purchasing.SupplierPaymentCorrections SET ReplacementPaymentId=@ReplacementId WHERE TenantId=@TenantId AND Id=@Group AND ReplacementPaymentId IS NULL;
              IF (SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl(@TenantId,@ReplacementId))<>1 THROW 51004,'Complete replacement payment evidence is unavailable.',1;
            END;
            SET @Result=(SELECT @Group groupId,@Group correctionId,@Payment originalPaymentId,@ReplacementId replacementPaymentId,
              @Journal reversalJournalId,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@TenantId AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
            IF @@ROWCOUNT<>1 THROW 51009,'Supplier correction receipt was already finalized.',1;
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
