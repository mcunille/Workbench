// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierAllocationCorrections
{

    internal const string ControlSql = """
        CREATE FUNCTION Purchasing.SupplierApplicationControl(@TenantId uniqueidentifier,@ApplicationId uniqueidentifier)
        RETURNS TABLE AS RETURN
          SELECT a.Id,a.FundingItemId,a.DebtItemId,a.Amount,a.PostingDate,a.GroupId,j.Id JournalId,
            CONVERT(bit,CASE s.SourceKind WHEN 'SupplierPayment' THEN 1 ELSE 0 END) Embedded,
            f.AccountId FundingAccountId,f.AccountVersion FundingAccountVersion,
            f.AccountCode FundingCode,f.AccountName FundingName,f.AccountType FundingType,f.AccountPurpose FundingPurpose,
            d.AccountId DebtAccountId,d.AccountVersion DebtAccountVersion,
            d.AccountCode DebtCode,d.AccountName DebtName,d.AccountType DebtType,d.AccountPurpose DebtPurpose
          FROM Purchasing.SupplierApplications a
          CROSS APPLY Purchasing.SupplierItemControl(@TenantId,a.FundingItemId) f
          CROSS APPLY Purchasing.SupplierItemControl(@TenantId,a.DebtItemId) d
          JOIN Accounting.SourceEvents s ON s.TenantId=a.TenantId AND s.PostingDate=a.PostingDate AND s.RecordedAtUtc=a.RecordedAtUtc
            AND ((s.SourceKind='SupplierApplication' AND s.SourceId=a.Id AND s.SourceRevision=a.GroupId AND s.EventKind='Apply')
              OR (s.SourceKind='SupplierPayment' AND s.SourceId=a.FundingItemId AND s.EventKind='Payment'))
          JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.RecordedAtUtc=a.RecordedAtUtc AND j.PostingDate=a.PostingDate
          JOIN Accounting.PostingReceipts r ON r.TenantId=j.TenantId AND r.JournalId=j.Id AND r.SourceEventId=s.Id AND r.RecordedAtUtc=a.RecordedAtUtc AND r.ActorId=a.ActorId
          WHERE a.TenantId=@TenantId AND a.Id=@ApplicationId AND s.ActorId=a.ActorId
            AND ((s.SourceKind='SupplierPayment' AND r.SourceCommandKind='Supplier.Payment'
                AND EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentControl(@TenantId,a.FundingItemId))
                AND EXISTS(SELECT 1 FROM OPENJSON(s.SnapshotJson,'$.allocations') e
                  WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.applicationId'))=a.Id
                    AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId'))=a.DebtItemId
                    AND TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount'))=a.Amount))
              OR (s.SourceKind='SupplierApplication' AND r.SourceCommandKind='Supplier.Apply'
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.applicationId'))=a.Id
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.fundingItemId'))=a.FundingItemId
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.debtItemId'))=a.DebtItemId
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.groupId'))=a.GroupId
                AND TRY_CONVERT(decimal(28,4),JSON_VALUE(s.SnapshotJson,'$.amount'))=a.Amount
                AND (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=j.Id)=2
                AND (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=@TenantId AND m.SourceEventId=s.Id)=2
                AND (SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions c WHERE c.TenantId=@TenantId AND c.JournalId=j.Id)=2
                AND NOT EXISTS(SELECT 1 FROM (VALUES(a.DebtItemId,1,d.AccountId,d.AccountVersion),(a.FundingItemId,2,f.AccountId,f.AccountVersion)) e(ItemId,Ordinal,AccountId,AccountVersion)
                  WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m
                    JOIN Purchasing.SupplierControlAttributions c ON c.TenantId=m.TenantId AND c.MovementId=m.Id AND c.GroupId=m.GroupId AND c.Amount=m.Amount
                    JOIN Accounting.JournalLines l ON l.TenantId=c.TenantId AND l.JournalId=c.JournalId AND l.Ordinal=c.Ordinal
                    WHERE m.TenantId=@TenantId AND m.SourceEventId=s.Id AND m.GroupId=a.GroupId AND m.ItemId=e.ItemId
                      AND m.EventKind='Apply' AND m.Amount=-a.Amount AND m.PostingDate=a.PostingDate AND m.RecordedAtUtc=a.RecordedAtUtc
                      AND c.JournalId=j.Id AND c.Ordinal=e.Ordinal AND c.AccountId=e.AccountId AND c.AccountVersion=e.AccountVersion
                      AND l.AccountId=c.AccountId AND l.AccountVersion=c.AccountVersion AND l.AccountPurpose=c.AccountPurpose
                      AND ((e.Ordinal=1 AND l.Debit=a.Amount AND l.Credit=0) OR (e.Ordinal=2 AND l.Credit=a.Amount AND l.Debit=0)))<>1)));
        """;

    internal const string KernelSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.PostJournal')),
          @Anchor nvarchar(max)=N'IF EXISTS(SELECT 1 FROM @Input i LEFT JOIN Accounting.Accounts';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0
          OR CHARINDEX(N'IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind<>N''Supplier.Payment''',@Definition)=0
          THROW 50020,'Unsupported application inverse kernel predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,N'IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind<>N''Supplier.Payment''',
          N'IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind NOT IN(N''Supplier.Payment'',N''Supplier.ReverseApplication'')');
        SET @Definition=REPLACE(@Definition,@Anchor,N'
          IF @SourceCommandKind=N''Supplier.ReverseApplication''
          BEGIN
            IF @SourceKind<>N''SupplierApplicationReversal'' OR @EventKind<>N''Reverse'' OR @RequiredPermission<>N''SupplierAllocationsManage''
              OR @SupplierControlItems IS NULL
              THROW 51004,''Invalid supplier application inverse authority.'',1;
          END;
          '+@Anchor);
        -- The input and source proof below are server-derived. No caller account snapshot is admitted.
        SET @Definition=REPLACE(@Definition,N'Invalid supplier application inverse authority.'',1;',N'Invalid supplier application inverse authority.'',1;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N''SupplierAllocationsManage'';
            DECLARE @InverseApplication uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.applicationId''));
            IF (SELECT COUNT(*) FROM Purchasing.SupplierApplicationControl(@TenantId,@InverseApplication))<>1
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals WHERE TenantId=@TenantId AND ApplicationId=@InverseApplication)
              OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g
                ON g.TenantId=r.TenantId AND g.Id=r.GroupId WHERE r.TenantId=@TenantId AND r.GroupId=@SourceRevision AND r.ActorId=@ActorId
                AND JSON_VALUE(r.ResultJson,''$.state'')=''pending'' AND g.RecordedAtUtc=TRY_CONVERT(datetimeoffset,JSON_VALUE(r.ResultJson,''$.recordedAtUtc''))
                AND ((r.Operation=''ReverseSupplierApplication'' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,''$.applicationId''))=@InverseApplication)
                  OR (r.Operation=''CorrectSupplierPayment'' AND EXISTS(SELECT 1 FROM Purchasing.SupplierApplications a WHERE a.TenantId=@TenantId AND a.Id=@InverseApplication
                    AND a.FundingItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,''$.paymentId''))))))
              THROW 51004,''Supplier application inverse lacks pending owner evidence.'',1;
            INSERT @Historical SELECT e.Ordinal,e.AccountId,e.AccountVersion,e.AccountCode,e.AccountName,e.AccountType,e.AccountPurpose
              FROM Purchasing.SupplierApplicationControl(@TenantId,@InverseApplication) a
              CROSS APPLY(VALUES(1,a.DebtAccountId,a.DebtAccountVersion,a.DebtCode,a.DebtName,a.DebtType,a.DebtPurpose),
                (2,a.FundingAccountId,a.FundingAccountVersion,a.FundingCode,a.FundingName,a.FundingType,a.FundingPurpose))
                e(Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose)
              JOIN @Input l ON l.Ordinal=e.Ordinal AND l.AccountId=e.AccountId AND l.AccountVersion=e.AccountVersion
                AND ((e.Ordinal=1 AND l.Credit=a.Amount AND l.Debit=0) OR (e.Ordinal=2 AND l.Debit=a.Amount AND l.Credit=0))
              WHERE a.PostingDate<=@PostingDate AND @SourceRevision=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.groupId''))
                AND a.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(@SourceSnapshot,''$.amount''));
            IF (SELECT COUNT(*) FROM @Historical)<>2 OR (SELECT COUNT(*) FROM @Input)<>2
              THROW 51004,''Supplier application inverse must preserve historical controls.'',1;');
        EXEC sys.sp_executesql @Definition;
        """;

    internal const string ParticipantSql = """
        CREATE PROCEDURE Purchasing.PostSupplierApplicationInverse
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@ApplicationId uniqueidentifier,@GroupId uniqueidentifier,
          @RecordedAtUtc datetimeoffset,@ConfigurationVersion uniqueidentifier,@PostingDate date,@Reason nvarchar(max),
          @ReversalId uniqueidentifier OUTPUT,@JournalId uniqueidentifier OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
            @Lines nvarchar(max),@Snapshot nvarchar(max),@References nvarchar(max),@Request uniqueidentifier=NEWID();
          IF @@TRANCOUNT=0 OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Supplier inverse requires its owner transaction.',1;
          EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
          IF EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a
              JOIN Accounting.CorrectionGroups c ON c.TenantId=@TenantId AND c.OriginalJournalId=a.JournalId)
            THROW 51009,'Supplier application has an unsupported external correction.',1;
          SET @ReversalId=NEWID();
          SET @Snapshot=(SELECT @ApplicationId applicationId,@GroupId groupId,CONVERT(nvarchar(60),a.Amount) amount
            FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
          SET @Lines=(SELECT e.ordinal,e.accountId,e.accountVersion,CONVERT(nvarchar(60),e.debit) debit,CONVERT(nvarchar(60),e.credit) credit
            FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a CROSS APPLY
              (VALUES(1,a.DebtAccountId,a.DebtAccountVersion,CONVERT(decimal(28,4),0),a.Amount),
                (2,a.FundingAccountId,a.FundingAccountVersion,a.Amount,CONVERT(decimal(28,4),0))) e(ordinal,accountId,accountVersion,debit,credit)
            ORDER BY e.ordinal FOR JSON PATH);
          SET @References=N'[]';
          DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
          DECLARE @Currency varchar(3)=(SELECT i.Currency FROM Purchasing.SupplierApplications a
            JOIN Purchasing.SupplierOpenItems i ON i.TenantId=a.TenantId AND i.Id=a.FundingItemId WHERE a.TenantId=@TenantId AND a.Id=@ApplicationId);
          INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Supplier.ReverseApplication',1,
            @Snapshot,N'SupplierApplicationReversal',@ReversalId,@GroupId,N'Reverse',1,@ConfigurationVersion,@Currency,
            @PostingDate,@PostingDate,@PostingDate,NULL,@Reason,@Snapshot,@Lines,@References;
          SELECT @JournalId=JournalId FROM @Posted;
          UPDATE s SET RecordedAtUtc=@RecordedAtUtc FROM Accounting.SourceEvents s JOIN @Posted p ON p.SourceEventId=s.Id WHERE s.TenantId=@TenantId;
          UPDATE j SET RecordedAtUtc=@RecordedAtUtc FROM Accounting.JournalEntries j JOIN @Posted p ON p.JournalId=j.Id WHERE j.TenantId=@TenantId;
          UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@RecordedAtUtc WHERE TenantId=@TenantId AND RequestId=@Request;
          UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@RecordedAtUtc
            WHERE TenantId=@TenantId AND Action='Accounting.PostJournal' AND TargetId=@JournalId;
        END;
        """;

    internal const string EventsSql = """
          IF JSON_VALUE(@Events,'$[0].reversalId') IS NOT NULL
          BEGIN
            IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e WHERE (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
                OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('reversalId','journalId')
                  OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p GROUP BY e.[key],p.[key] HAVING COUNT(*)>1)
              THROW 51000,'Invalid supplier reversal event identities.',1;
            DECLARE @InverseApplications TABLE(Id uniqueidentifier PRIMARY KEY,ApplicationId uniqueidentifier UNIQUE,
              FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date,ActorId uniqueidentifier,
              Reason nvarchar(2000),SourceEventId uniqueidentifier,JournalId uniqueidentifier UNIQUE);
            INSERT @InverseApplications
              SELECT s.SourceId,a.Id,a.FundingItemId,a.DebtItemId,a.Amount,s.PostingDate,s.ActorId,s.Reason,s.Id,j.Id
              FROM OPENJSON(@Events) e
              JOIN Accounting.JournalEntries j ON j.TenantId=@TenantId AND j.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId'))
                AND j.RecordedAtUtc=@RecordedAtUtc
              JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
                AND s.SourceKind='SupplierApplicationReversal' AND s.EventKind='Reverse' AND s.SourceRevision=@GroupId
                AND s.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.reversalId'))
                AND s.RecordedAtUtc=@RecordedAtUtc AND s.PostingDate=j.PostingDate
              CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.applicationId'))) a
              JOIN Accounting.PostingReceipts r ON r.TenantId=j.TenantId AND r.SourceEventId=s.Id AND r.JournalId=j.Id
                AND r.SourceCommandKind='Supplier.ReverseApplication' AND r.ActorId=s.ActorId AND r.RecordedAtUtc=@RecordedAtUtc
              WHERE a.PostingDate<=s.PostingDate AND a.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(s.SnapshotJson,'$.amount'))
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.groupId'))=@GroupId;
            IF (SELECT COUNT(*) FROM @InverseApplications)<>@Count OR (SELECT COUNT(DISTINCT ActorId) FROM @InverseApplications)<>1
              OR EXISTS(SELECT 1 FROM @InverseApplications a JOIN Purchasing.SupplierApplicationReversals r
                ON r.TenantId=@TenantId AND r.ApplicationId=a.ApplicationId)
              OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g
                ON g.TenantId=r.TenantId AND g.Id=r.GroupId
                WHERE r.TenantId=@TenantId AND r.GroupId=@GroupId AND g.RecordedAtUtc=@RecordedAtUtc
                  AND JSON_VALUE(r.ResultJson,'$.state')='pending' AND r.ActorId=(SELECT TOP(1) ActorId FROM @InverseApplications)
                  AND r.Operation IN('ReverseSupplierApplication','CorrectSupplierPayment'))
              THROW 51004,'Authentic pending supplier reversal owner is unavailable.',1;
            DECLARE @InverseEffects TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,ApplicationId uniqueidentifier,
              SourceEventId uniqueidentifier,JournalId uniqueidentifier,Ordinal int,PostingDate date,Amount decimal(28,4));
            INSERT @InverseEffects SELECT NEWID(),e.ItemId,a.ApplicationId,a.SourceEventId,a.JournalId,e.Ordinal,a.PostingDate,a.Amount
              FROM @InverseApplications a CROSS APPLY(VALUES(a.DebtId,1),(a.FundingId,2)) e(ItemId,Ordinal);
            IF EXISTS(SELECT 1 FROM @InverseEffects e CROSS APPLY Purchasing.SupplierItemControl(@TenantId,e.ItemId) c
              WHERE NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.Ordinal=e.Ordinal
                AND l.AccountId=c.AccountId AND l.AccountVersion=c.AccountVersion AND l.AccountPurpose=c.AccountPurpose
                AND ((e.Ordinal=1 AND l.Credit=e.Amount AND l.Debit=0) OR (e.Ordinal=2 AND l.Debit=e.Amount AND l.Credit=0))))
              OR EXISTS(SELECT 1 FROM @InverseApplications a WHERE (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=a.JournalId)<>2)
              OR EXISTS(SELECT 1 FROM @InverseApplications a JOIN Purchasing.SupplierControlAttributions c ON c.TenantId=@TenantId AND c.JournalId=a.JournalId)
              THROW 51004,'Supplier reversal control evidence is not exact.',1;
            DECLARE @InverseMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,PostingDate postingDate,
              CONVERT(nvarchar(60),Amount) amount FROM @InverseEffects FOR JSON PATH);
            EXEC Purchasing.AssertSupplierAvailability @TenantId,@InverseMovements;
            INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
              SELECT @TenantId,Id,@GroupId,ApplicationId,PostingDate,Reason,ActorId,@RecordedAtUtc FROM @InverseApplications;
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              SELECT @TenantId,Id,ItemId,@GroupId,'ReverseApplication',SourceEventId,PostingDate,Amount,@RecordedAtUtc FROM @InverseEffects;
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @TenantId,NEWID(),@GroupId,e.Id,e.JournalId,e.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,e.Amount
              FROM @InverseEffects e JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.Ordinal=e.Ordinal;
            UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v WHERE v.TenantId=@TenantId
              AND EXISTS(SELECT 1 FROM @InverseEffects e WHERE e.ItemId=v.ItemId);
            UPDATE v SET ApplicationId=v.ApplicationId FROM Purchasing.SupplierApplicationVersions v WHERE v.TenantId=@TenantId
              AND EXISTS(SELECT 1 FROM @InverseApplications a WHERE a.ApplicationId=v.ApplicationId);
            RETURN;
          END;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.ReverseSupplierApplication
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
            IF @TenantId IS NULL THROW 51003,'Current supplier allocation authority is required.',1;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
            BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier allocation authority is required.',1; THROW; END CATCH;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
            EXEC Purchasing.ValidateSupplierFinancialCommand N'ReverseSupplierApplication',@Command,@Canonical OUTPUT;
            IF (SELECT COUNT(*) FROM OPENJSON(@Canonical) WHERE [key]<>'reapplications')<>11
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
                ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion',
                 'supplierId','currency','postingDate','applicationId','expectedApplicationVersion','reason','reapplications')
                OR ([key]='schemaVersion' AND type<>2) OR ([key]='reapplications' AND type<>4)
                OR ([key] NOT IN('schemaVersion','reapplications') AND type<>1))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','applicationId')
                AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedPurchaseOrderVersion','expectedApplicationVersion')
                AND (DATALENGTH(value)<>36 OR TRY_CONVERT(binary(8),value,1) IS NULL))
              OR DATALENGTH(JSON_VALUE(@Canonical,'$.postingDate'))<>20 OR TRY_CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23) IS NULL
              OR DATALENGTH(JSON_VALUE(@Canonical,'$.currency'))<>6
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key]='reason' AND (DATALENGTH(value)>4000 OR LEN(TRIM(value))=0))
              THROW 51000,'Invalid supplier reversal fields.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                AND Operation='ReverseSupplierApplication' AND ActorId=@ActorId
                AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)
                AND COALESCE(JSON_VALUE(ResultJson,'$.state'),'complete')<>'pending')
                THROW 51009,'Request identity has different or pending content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            DECLARE @Application uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.applicationId')),
              @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
              @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
              @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
              @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Reason nvarchar(max)=JSON_VALUE(@Canonical,'$.reason'),
              @Group uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME(),@Journal uniqueidentifier,@Reversal uniqueidentifier;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po
                AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1)
                AND SupplierId=@Supplier AND Currency=JSON_VALUE(@Canonical,'$.currency') AND State='Ordered' AND IsDeleted=0)
              OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationVersions v WITH(UPDLOCK,HOLDLOCK)
                JOIN Purchasing.SupplierApplications a ON a.TenantId=v.TenantId AND a.Id=v.ApplicationId
                JOIN Purchasing.SupplierOpenItems i ON i.TenantId=a.TenantId AND i.Id=a.FundingItemId
                WHERE v.TenantId=@TenantId AND v.ApplicationId=@Application AND v.RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedApplicationVersion'),1)
                  AND i.SupplierId=@Supplier AND i.PurchaseOrderId=@Po AND i.Currency=JSON_VALUE(@Canonical,'$.currency'))
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals WHERE TenantId=@TenantId AND ApplicationId=@Application)
              THROW 51009,'Supplier application or purchase revision changed.',1;
            IF (SELECT COUNT(*) FROM Purchasing.SupplierApplicationControl(@TenantId,@Application))<>1
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND Id=@Application AND PostingDate>@Date)
              THROW 51004,'Historical supplier application is unavailable on the reversal date.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.reapplications') e WHERE e.type<>5
                OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>6
                OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN
                    ('fundingItemId','expectedFundingItemVersion','billId','itemId','expectedItemVersion','amount') OR p.type<>1
                    OR (p.[key] IN('expectedFundingItemVersion','expectedItemVersion')
                      AND (DATALENGTH(p.value)<>36 OR TRY_CONVERT(binary(8),p.value,1) IS NULL)))
                OR TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount'))<=0)
              THROW 51000,'Invalid explicit supplier reapplications.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.reapplications') e WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK)
                  WHERE v.TenantId=@TenantId AND v.ItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.fundingItemId'))
                    AND v.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(e.value,'$.expectedFundingItemVersion'),1)))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.reapplications') t
                WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK) WHERE v.TenantId=@TenantId
                  AND v.ItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(t.value,'$.itemId')) AND v.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(t.value,'$.expectedItemVersion'),1)))
              THROW 51009,'Explicit reapplication item revision changed.',1;
            EXEC Accounting.EnsureOpenPeriod @PostingDate=@Date,@ExpectedConfigurationVersion=@Config;
            INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc)
              VALUES(@TenantId,@Group,'ReverseApplication',@Application,@Now);
            SET @Result=(SELECT 'pending' state,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@RequestId,@Group,'ReverseSupplierApplication',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
            EXEC Purchasing.PostSupplierApplicationInverse @ActorId,@SessionId,@Application,@Group,@Now,@Config,@Date,@Reason,@Reversal OUTPUT,@Journal OUTPUT;
            DECLARE @Events nvarchar(max)=(SELECT @Reversal reversalId,@Journal journalId FOR JSON PATH);
            EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@Events;
            DECLARE @Reapply nvarchar(max),@ReapplyCanonical nvarchar(max),@ReapplyRequest uniqueidentifier,@FundingVersion varchar(18),
              @TargetVersion varchar(18),@Reapplications nvarchar(max)=JSON_QUERY(@Canonical,'$.reapplications');
            DECLARE reapplications CURSOR LOCAL FAST_FORWARD FOR SELECT value FROM OPENJSON(@Reapplications) ORDER BY CONVERT(int,[key]);
            OPEN reapplications; FETCH NEXT FROM reapplications INTO @Reapply;
            WHILE @@FETCH_STATUS=0
            BEGIN
              SELECT @FundingVersion=CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierItemVersions
                WHERE TenantId=@TenantId AND ItemId=CONVERT(uniqueidentifier,JSON_VALUE(@Reapply,'$.fundingItemId'));
              SELECT @TargetVersion=CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierItemVersions
                WHERE TenantId=@TenantId AND ItemId=CONVERT(uniqueidentifier,JSON_VALUE(@Reapply,'$.itemId'));
              DECLARE @Targets nvarchar(max)=(SELECT JSON_VALUE(@Reapply,'$.billId') billId,JSON_VALUE(@Reapply,'$.itemId') itemId,
                @TargetVersion expectedItemVersion,JSON_VALUE(@Reapply,'$.amount') amount FOR JSON PATH);
              SET @Reapply=(SELECT 1 schemaVersion,'ApplySupplierFunds' operation,@Config expectedConfigurationVersion,
                @Po purchaseOrderId,JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion') expectedPurchaseOrderVersion,@Supplier supplierId,
                JSON_VALUE(@Canonical,'$.currency') currency,@Date postingDate,JSON_VALUE(@Reapply,'$.fundingItemId') fundingItemId,
                @FundingVersion expectedFundingItemVersion,JSON_QUERY(@Targets) targets FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              EXEC Purchasing.ValidateSupplierFinancialCommand N'ApplySupplierFunds',@Reapply,@ReapplyCanonical OUTPUT;
              SET @ReapplyRequest=NEWID();
              DECLARE @Compositions nvarchar(max)=(SELECT @ReapplyRequest requestId,'ApplySupplierFunds' operation,@ReapplyCanonical canonicalInput FOR JSON PATH);
              UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=JSON_MODIFY(ResultJson,'$.compositions',JSON_QUERY(@Compositions))
                WHERE TenantId=@TenantId AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
              IF @@ROWCOUNT<>1 THROW 51009,'Supplier reversal receipt was already finalized.',1;
              EXEC Purchasing.ApplySupplierFundsCore @ActorId,@SessionId,@ReapplyRequest,@ReapplyCanonical,@RequestId;
              FETCH NEXT FROM reapplications INTO @Reapply;
            END;
            CLOSE reapplications; DEALLOCATE reapplications;
            SET @Result=(SELECT @Group groupId,@Reversal reversalId,@Application applicationId,@Journal journalId,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@TenantId AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
            IF @@ROWCOUNT<>1 THROW 51009,'Supplier reversal receipt was already finalized.',1;
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

}
