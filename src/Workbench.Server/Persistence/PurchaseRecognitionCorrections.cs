// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class PurchaseRecognitionCorrections
{
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.CorrectRecognition
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @RequiredPermission nvarchar(max),@Command nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL THROW 51003,'Current source authority is required.',1;
          IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
            THROW 51000,'A bounded source permission is required.',1;
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
          BEGIN TRY
            EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;
          END TRY BEGIN CATCH
            IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1;
            THROW;
          END CATCH;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
            THROW 51000,'Invalid correction request.',1;
          DECLARE @Canonical nvarchar(max);
          EXEC Purchasing.CanonicalizeRecognition @Command,1,@Canonical OUTPUT;
          DECLARE @Operation varchar(16)=JSON_VALUE(@Canonical,'$.operation');
          IF EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
              AND ActorId=@ActorId AND CommandKind=@Operation AND CommandVersion=1 AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
              THROW 51009,'This request identifier was already used with different content.',1;
            SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
            RETURN;
          END;
          DECLARE @ConfigurationVersion uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
            @PurchaseOrderId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
            @UnitId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.unitId')),
            @PostingDate date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
            @PurchaseOrderVersion binary(8)=TRY_CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1),
            @Reason nvarchar(2000)=JSON_VALUE(@Canonical,'$.reason'),@SupplierId uniqueidentifier,@Currency varchar(3),@Replacement nvarchar(max)=JSON_QUERY(@Canonical,'$.replacement');
          IF @PurchaseOrderVersion IS NULL OR DATALENGTH(JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'))<>36
            OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Reason))=0
            THROW 51000,'Invalid correction version or reason.',1;
          SELECT @SupplierId=SupplierId,@Currency=Currency FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
            WHERE TenantId=@TenantId AND Id=@PurchaseOrderId AND IsDeleted=0 AND State='Ordered' AND RowVersion=@PurchaseOrderVersion;
          IF @SupplierId IS NULL THROW 51009,'Purchase order identity or revision changed.',1;
          IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@UnitId
            AND PurchaseOrderId=@PurchaseOrderId AND SupplierId=@SupplierId AND Currency=@Currency)
            THROW 51004,'Recognition unit is unavailable.',1;
          DECLARE @Originals TABLE(Id uniqueidentifier PRIMARY KEY,Side varchar(16),EventRevision int,JournalId uniqueidentifier,SourceId uniqueidentifier,
            SourceRevision uniqueidentifier,SourceComponentKey nvarchar(200),SubdivisionKey nvarchar(200),EffectiveDate date,PostingDate date,Sequence bigint);
          INSERT @Originals SELECT e.Id,e.Side,e.EventRevision,e.JournalId,e.SourceId,e.SourceRevision,e.SourceComponentKey,e.SubdivisionKey,e.EffectiveDate,e.PostingDate,j.Sequence
            FROM Purchasing.ActiveRecognitionSideEvents e WITH(UPDLOCK,HOLDLOCK)
            LEFT JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.Id=e.JournalId WHERE e.TenantId=@TenantId AND e.UnitId=@UnitId;
          IF NOT EXISTS(SELECT 1 FROM @Originals) OR EXISTS(SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND UnitId=@UnitId)
            THROW 51009,'This unit was already corrected.',1;
          IF (SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.expectedEventRevisions'))<>(SELECT COUNT(*) FROM @Originals)
            OR EXISTS(SELECT JSON_VALUE(value,'$.side') FROM OPENJSON(@Canonical,'$.expectedEventRevisions') GROUP BY JSON_VALUE(value,'$.side') HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.expectedEventRevisions') r LEFT JOIN @Originals e
              ON e.Side COLLATE Latin1_General_100_BIN2=JSON_VALUE(r.value,'$.side') AND e.EventRevision=TRY_CONVERT(int,JSON_VALUE(r.value,'$.eventRevision')) WHERE e.Id IS NULL)
            THROW 51009,'The complete current event revisions are required.',1;
          IF EXISTS(SELECT 1 FROM @Originals WHERE PostingDate>@PostingDate)
            THROW 51000,'The correction cannot predate an affected posting.',1;
          DECLARE @OriginalMatchId uniqueidentifier,@ReplacementMatchId uniqueidentifier=NULL,@ReplacementUnitId uniqueidentifier=NULL;
          SELECT @OriginalMatchId=Id FROM Purchasing.RecognitionMatches WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND UnitId=@UnitId;
          IF ((SELECT COUNT(*) FROM @Originals)=2 AND @OriginalMatchId IS NULL)
            OR EXISTS(SELECT 1 FROM Purchasing.RecognitionMatches m WHERE m.TenantId=@TenantId AND m.Id=@OriginalMatchId
              AND (NOT EXISTS(SELECT 1 FROM @Originals WHERE Id=m.RecognitionEventId) OR NOT EXISTS(SELECT 1 FROM @Originals WHERE Id=m.InvoiceEventId)))
            THROW 51009,'Recognition dependency history is incomplete.',1;
          IF @Operation='Replace'
          BEGIN
            SET @ReplacementUnitId=CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.unitId'));
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@ReplacementUnitId)
              OR TRY_CONVERT(int,JSON_VALUE(@Replacement,'$.expectedPriorEventRevision'))<>0
              THROW 51009,'Replacement requires a fresh successor unit.',1;
            IF (SELECT COUNT(*) FROM OPENJSON(@Replacement,'$.sides'))<>(SELECT COUNT(*) FROM @Originals)
              OR EXISTS(SELECT JSON_VALUE(value,'$.side') FROM OPENJSON(@Replacement,'$.sides') GROUP BY JSON_VALUE(value,'$.side') HAVING COUNT(*)>1)
              OR EXISTS(SELECT 1 FROM OPENJSON(@Replacement,'$.sides') s LEFT JOIN @Originals e ON e.Side COLLATE Latin1_General_100_BIN2=JSON_VALUE(s.value,'$.side')
                WHERE e.Id IS NULL OR TRY_CONVERT(int,JSON_VALUE(s.value,'$.eventRevision'))<>e.EventRevision+1
                  OR CONVERT(uniqueidentifier,JSON_VALUE(s.value,'$.sourceId'))<>e.SourceId
                  OR CONVERT(uniqueidentifier,JSON_VALUE(s.value,'$.sourceRevision'))=e.SourceRevision
                  OR CONVERT(varbinary(max),JSON_VALUE(s.value,'$.sourceComponentKey'))<>CONVERT(varbinary(max),e.SourceComponentKey)
                  OR CONVERT(varbinary(max),JSON_VALUE(s.value,'$.subdivisionKey'))<>CONVERT(varbinary(max),e.SubdivisionKey)
                  OR CONVERT(date,JSON_VALUE(s.value,'$.effectiveDate'),23)<>e.EffectiveDate)
              THROW 51009,'Replacement must preserve the complete source identity and effective dates with new revisions.',1;
          END;
          EXEC Accounting.EnsureOpenPeriod @PostingDate,@ConfigurationVersion;
          DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@GroupId uniqueidentifier=NEWID(),@CommandId uniqueidentifier=NEWID();
          INSERT Purchasing.RecognitionCorrectionGroups(TenantId,Id,UnitId,OriginalMatchId,ActorId,Operation,PostingDate,Reason,RecordedAtUtc)
            VALUES(@TenantId,@GroupId,@UnitId,@OriginalMatchId,@ActorId,@Operation,@PostingDate,@Reason,@Now);
          INSERT Purchasing.RecognitionEventCorrections(TenantId,OriginalEventId,CorrectionGroupId) SELECT @TenantId,Id,@GroupId FROM @Originals;
          DECLARE @OriginalId uniqueidentifier,@JournalId uniqueidentifier,@Evidence nvarchar(max),@KernelRequestId uniqueidentifier;
          DECLARE @Kernel TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
          DECLARE @Journals TABLE(Id uniqueidentifier PRIMARY KEY);
          -- Reverse the dependent clearing journal before its first-side journal. Zero sides still have correction links.
          DECLARE reverse_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Id,JournalId FROM @Originals WHERE JournalId IS NOT NULL ORDER BY Sequence DESC;
          OPEN reverse_cursor; FETCH NEXT FROM reverse_cursor INTO @OriginalId,@JournalId;
          WHILE @@FETCH_STATUS=0
          BEGIN
            SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),e.SourceRevision) originalSourceRevision,CONVERT(varchar(64),e.SnapshotSha256,2) originalSnapshotSha256
              FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents e ON e.TenantId=j.TenantId AND e.Id=j.SourceEventId
              WHERE j.TenantId=@TenantId AND j.Id=@JournalId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            SET @KernelRequestId=NEWID(); DELETE @Kernel;
            INSERT @Kernel EXEC Accounting.CorrectJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@KernelRequestId,
              @RequiredPermission=@RequiredPermission,@SourceCommandKind=N'PurchaseRecognition.CorrectInternal',@SourceCommandVersion=1,
              @CanonicalInput=@Canonical,@OriginalJournalId=@JournalId,@ExpectedConfigurationVersion=@ConfigurationVersion,
              @PostingDate=@PostingDate,@Reason=@Reason,@Evidence=@Evidence;
            UPDATE c SET AccountingCorrectionGroupId=k.CorrectionId FROM Purchasing.RecognitionEventCorrections c CROSS JOIN @Kernel k
              WHERE c.TenantId=@TenantId AND c.OriginalEventId=@OriginalId;
            INSERT @Journals SELECT ReversalJournalId FROM @Kernel;
            FETCH NEXT FROM reverse_cursor INTO @OriginalId,@JournalId;
          END;
          CLOSE reverse_cursor; DEALLOCATE reverse_cursor;
          DECLARE @PostedResult nvarchar(max)=NULL;
          IF @Operation='Replace'
          BEGIN
            -- The strict replacement is caller evidence, without SQL-owned accountMappings. Original snapshots are never reconstructed or modified.
            DECLARE @PostInput nvarchar(max)=(SELECT 1 schemaVersion,'Post' operation,@ConfigurationVersion expectedConfigurationVersion,@PurchaseOrderId purchaseOrderId,
              JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion') expectedPurchaseOrderVersion,@SupplierId supplierId,@Currency currency,@PostingDate postingDate,
              JSON_QUERY(N'['+@Replacement+N']') units FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),@PostRequestId uniqueidentifier=NEWID();
            EXEC Purchasing.PostRecognition @ActorId,@SessionId,@PostRequestId,@RequiredPermission,@PostInput,@GroupId,@PostedResult OUTPUT;
            SELECT @ReplacementMatchId=Id FROM Purchasing.RecognitionMatches WHERE TenantId=@TenantId AND UnitId=@ReplacementUnitId;
            UPDATE c SET ReplacementEventId=e.Id FROM Purchasing.RecognitionEventCorrections c JOIN @Originals o ON o.Id=c.OriginalEventId
              JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=c.TenantId AND e.UnitId=@ReplacementUnitId AND e.Side=o.Side WHERE c.TenantId=@TenantId AND c.CorrectionGroupId=@GroupId;
            INSERT @Journals SELECT CONVERT(uniqueidentifier,value) FROM OPENJSON(@PostedResult,'$.journalIds');
          END;
          UPDATE Purchasing.RecognitionCorrectionGroups SET ReplacementUnitId=@ReplacementUnitId,ReplacementMatchId=@ReplacementMatchId WHERE TenantId=@TenantId AND Id=@GroupId;
          -- Normalize only newly inserted inverse rows. Replacement posting inherited the outer instant directly.
          UPDATE j SET RecordedAtUtc=@Now FROM Accounting.JournalEntries j JOIN @Journals n ON n.Id=j.Id WHERE j.TenantId=@TenantId;
          UPDATE e SET RecordedAtUtc=@Now FROM Accounting.SourceEvents e JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.SourceEventId=e.Id JOIN @Journals n ON n.Id=j.Id WHERE e.TenantId=@TenantId;
          UPDATE p SET RecordedAtUtc=@Now FROM Accounting.PostingReceipts p JOIN @Journals n ON n.Id=p.JournalId WHERE p.TenantId=@TenantId;
          UPDATE g SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups g JOIN Purchasing.RecognitionEventCorrections c ON c.TenantId=g.TenantId AND c.AccountingCorrectionGroupId=g.Id WHERE c.TenantId=@TenantId AND c.CorrectionGroupId=@GroupId;
          UPDATE a SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents a WHERE a.TenantId=@TenantId AND
            ((a.Action='Accounting.PostJournal' AND a.TargetId IN (SELECT Id FROM @Journals)) OR
             (a.Action='Accounting.CorrectJournal' AND a.TargetId IN (SELECT AccountingCorrectionGroupId FROM Purchasing.RecognitionEventCorrections WHERE TenantId=@TenantId AND CorrectionGroupId=@GroupId)));
          DECLARE @UnitIds nvarchar(max)=N'["'+CONVERT(nvarchar(36),@UnitId)+N'"'+CASE WHEN @ReplacementUnitId IS NULL THEN N'' ELSE N',"'+CONVERT(nvarchar(36),@ReplacementUnitId)+N'"' END+N']',
            @EventIds nvarchar(max)=COALESCE(JSON_QUERY(@PostedResult,'$.eventIds'),N'[]'),
            @MatchIds nvarchar(max)=N'['+CASE WHEN @OriginalMatchId IS NULL THEN N'' ELSE N'"'+CONVERT(nvarchar(36),@OriginalMatchId)+N'"' END+
              CASE WHEN @ReplacementMatchId IS NULL THEN N'' ELSE N',"'+CONVERT(nvarchar(36),@ReplacementMatchId)+N'"' END+N']',
            @JournalIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id),N'')+N']' FROM @Journals),@Result nvarchar(max);
          SET @Result=(SELECT @CommandId commandId,JSON_QUERY(@UnitIds) unitIds,JSON_QUERY(@EventIds) eventIds,JSON_QUERY(@MatchIds) matchIds,
            @GroupId correctionGroupId,JSON_QUERY(@JournalIds) journalIds,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
          INSERT Purchasing.RecognitionGroupReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
            VALUES(@TenantId,@RequestId,@ActorId,@Operation,1,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
          INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
            VALUES(NEWID(),@TenantId,@ActorId,N'Purchasing.CorrectRecognition',N'RecognitionCorrection',@GroupId,@Now);
          SELECT @Result ResultJson;
        END;
        """;
}
