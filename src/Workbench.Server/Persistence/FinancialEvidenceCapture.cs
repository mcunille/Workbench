// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceCapture
{
    internal const string DeadlineSql = """
        CREATE FUNCTION Accounting.FinancialRetentionDeadline(@PostingDate date,@RecordedAtUtc datetimeoffset,@Years int)
        RETURNS datetimeoffset AS BEGIN
          IF @Years IS NULL OR @Years NOT BETWEEN 1 AND 1000 OR @PostingDate IS NULL OR @RecordedAtUtc IS NULL RETURN NULL;
          DECLARE @Anchor datetimeoffset=CASE WHEN CONVERT(datetimeoffset,@PostingDate)>@RecordedAtUtc
            THEN CONVERT(datetimeoffset,@PostingDate) ELSE SWITCHOFFSET(@RecordedAtUtc,'+00:00') END;
          IF DATEPART(year,@Anchor)>9999-@Years RETURN NULL;
          RETURN DATEADD(year,@Years,@Anchor);
        END;
        """;

    internal const string AcquireSql = """
        CREATE PROCEDURE Accounting.AcquireFinancialEvidenceLink
          @EvidenceSetId uniqueidentifier,@DocumentId uniqueidentifier,@RevisionId uniqueidentifier,
          @OriginalLabel nvarchar(200),@ExpectedDigest varchar(64),@ExpectedLength bigint,@LinkId uniqueidentifier OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 THROW 51000,'Evidence acquisition requires a source transaction.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
            @Po uniqueidentifier,@Date date,@Now datetimeoffset=SYSUTCDATETIME(),@Config uniqueidentifier,@Years int,@Rationale nvarchar(2000),
            @Deadline datetimeoffset,@Anchor datetimeoffset,@Attachment uniqueidentifier,@Label nvarchar(200),@Digest varchar(64),@Length bigint,
            @Media nvarchar(100),@Extension nvarchar(10),@Payload nvarchar(max),@Validated nvarchar(max);
          SELECT @Po=PurchaseOrderId,@Date=PostingDate FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@EvidenceSetId;
          IF @Po IS NULL THROW 51004,'Evidence owner unavailable.',1;
          SET @Payload=(SELECT JSON_QUERY((SELECT @DocumentId documentId,@RevisionId revisionId FOR JSON PATH)) documents FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
          EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@Payload,@Validated OUTPUT;
          SELECT @Attachment=AttachmentId,@Label=Label,@Digest=Sha256,@Length=Length,@Media=MediaType,@Extension=Extension
            FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@DocumentId AND OrderId=@Po AND RevisionId=@RevisionId;
          IF (@ExpectedDigest IS NOT NULL AND @ExpectedDigest<>@Digest) OR (@ExpectedLength IS NOT NULL AND @ExpectedLength<>@Length)
            THROW 51004,'Posted document content no longer matches its source.',1;
          SELECT @Config=Version,@Years=TRY_CONVERT(int,JSON_VALUE(Payload,'$.policies.retentionYears')),
            @Rationale=JSON_VALUE(Payload,'$.policies.retentionRationale') FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId;
          IF @Config IS NULL OR (@Years IS NOT NULL AND @Years NOT BETWEEN 1 AND 1000) THROW 51000,'Invalid evidence retention policy.',1;
          SET @Anchor=CASE WHEN CONVERT(datetimeoffset,@Date)>@Now THEN CONVERT(datetimeoffset,@Date) ELSE @Now END;
          SET @Deadline=Accounting.FinancialRetentionDeadline(@Date,@Now,@Years);
          IF @Years IS NOT NULL AND @Deadline IS NULL THROW 51000,'Evidence retention deadline exceeds the supported calendar.',1;
          IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND EvidenceSetId=@EvidenceSetId AND DocumentId=@DocumentId AND RevisionId=@RevisionId)
            THROW 51009,'This revision already belongs to the evidence set.',1;
          SET @LinkId=NEWID();
          INSERT Accounting.FinancialEvidenceLinks(TenantId,Id,EvidenceSetId,DocumentId,AttachmentId,RevisionId,Sha256,Length,Label,MediaType,Extension,
            ConfigurationVersion,RetentionYears,RetentionRationale,AnchorAtUtc,MinimumRetentionDeadlineUtc,RecordedAtUtc)
            VALUES(@TenantId,@LinkId,@EvidenceSetId,@DocumentId,@Attachment,@RevisionId,@Digest,@Length,COALESCE(@OriginalLabel,@Label),@Media,@Extension,
              @Config,@Years,@Rationale,@Anchor,@Deadline,@Now);
          UPDATE Storage.Attachments SET Held=1 WHERE TenantId=@TenantId AND Id=@Attachment;
          UPDATE Storage.FinancialEvidenceAttachmentStates SET AttachmentId=AttachmentId WHERE TenantId=@TenantId AND AttachmentId=@Attachment;
          IF @@ROWCOUNT=0 INSERT Storage.FinancialEvidenceAttachmentStates(TenantId,AttachmentId) VALUES(@TenantId,@Attachment);
        END;
        """;

    internal const string CaptureSql = """
        CREATE PROCEDURE Accounting.CaptureFinancialEvidence @OwnerKind varchar(32),@OwnerId uniqueidentifier,
          @OwnerRevisionId uniqueidentifier,@EvidenceSetId uniqueidentifier OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @@TRANCOUNT=0 OR @TenantId IS NULL THROW 51000,'Evidence capture requires an authenticated source transaction.',1;
          IF APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Evidence capture requires source coordination.',1;
          SET @EvidenceSetId=NULL;
          SELECT @EvidenceSetId=Id FROM Accounting.FinancialEvidenceSets
            WHERE TenantId=@TenantId AND OwnerKind=@OwnerKind AND OwnerId=@OwnerId AND OwnerRevisionId=@OwnerRevisionId;
          IF @EvidenceSetId IS NOT NULL RETURN;
          DECLARE @Po uniqueidentifier,@Supplier uniqueidentifier,@Actor uniqueidentifier,@Date date,@Recorded datetimeoffset,
            @Hash binary(32),@Documents nvarchar(max),@Missing nvarchar(2000),@Legacy bit,@Permission nvarchar(100),@Inherited uniqueidentifier;
          SELECT @Po=PurchaseOrderId,@Supplier=SupplierId,@Actor=ActorId,@Date=PostingDate,@Recorded=RecordedAtUtc,
            @Hash=SourceSnapshotSha256,@Documents=Documents,@Missing=MissingEvidenceReason,@Legacy=LegacyEvidence,
            @Permission=MutationPermission,@Inherited=InheritedEvidenceSetId
            FROM Accounting.FinancialEvidenceSource(@TenantId,@OwnerKind,@OwnerId,@OwnerRevisionId);
          IF @Po IS NULL THROW 51004,'Authentic posted evidence owner required.',1;
          IF NOT EXISTS(SELECT 1 FROM OPENJSON(@Documents)) AND NULLIF(TRIM(@Missing),'') IS NULL
            THROW 51000,'A source without documents requires an explicit missing-evidence reason.',1;
          DECLARE @Payload nvarchar(max)=(SELECT JSON_QUERY(COALESCE(@Documents,N'[]')) documents FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),@Validated nvarchar(max);
          EXEC Accounting.ValidateInheritedFinancialEvidence @TenantId,@Po,@Payload,@Inherited,@Validated OUTPUT;
          SET @EvidenceSetId=NEWID();
          INSERT Accounting.FinancialEvidenceSets(TenantId,Id,OwnerKind,OwnerId,OwnerRevisionId,PurchaseOrderId,SupplierId,ActorId,PostingDate,RecordedAtUtc,
            SourceSnapshotSha256,MissingEvidenceReason,LegacyEvidence,MutationPermission,InheritedEvidenceSetId)
            VALUES(@TenantId,@EvidenceSetId,@OwnerKind,@OwnerId,@OwnerRevisionId,@Po,@Supplier,@Actor,@Date,@Recorded,@Hash,@Missing,@Legacy,@Permission,@Inherited);
          DECLARE @Document uniqueidentifier,@Revision uniqueidentifier,@Label nvarchar(200),@Digest varchar(64),@Length bigint,@Link uniqueidentifier;
          DECLARE source_documents CURSOR LOCAL FAST_FORWARD FOR
            SELECT CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')),CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId')),
              JSON_VALUE(j.value,'$.label'),JSON_VALUE(j.value,'$.digest'),TRY_CONVERT(bigint,JSON_VALUE(j.value,'$.length'))
            FROM OPENJSON(@Documents) j JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
            WHERE NOT EXISTS(SELECT 1 FROM Accounting.InheritedFinancialEvidence(@TenantId,@Inherited) l WHERE l.DocumentId=d.Id AND l.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId')))
            ORDER BY d.AttachmentId,d.RevisionId,d.Id;
          OPEN source_documents; FETCH NEXT FROM source_documents INTO @Document,@Revision,@Label,@Digest,@Length;
          WHILE @@FETCH_STATUS=0
          BEGIN
            EXEC Accounting.AcquireFinancialEvidenceLink @EvidenceSetId,@Document,@Revision,@Label,@Digest,@Length,@Link OUTPUT;
            FETCH NEXT FROM source_documents INTO @Document,@Revision,@Label,@Digest,@Length;
          END;
          CLOSE source_documents; DEALLOCATE source_documents;
        END;
        """;

    internal const string AppendSql = """
        CREATE PROCEDURE Accounting.AppendFinancialEvidence @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            IF @Command IS NULL OR ISJSON(@Command,OBJECT)<>1 OR DATALENGTH(@Command)>262144
              OR @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid evidence addition envelope.',1;
            IF (SELECT COUNT(*) FROM OPENJSON(@Command))<>9
              OR EXISTS(SELECT [key] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@Command) GROUP BY [key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
              OR EXISTS(SELECT 1 FROM OPENJSON(@Command) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key]))
                OR [key] COLLATE Latin1_General_100_BIN2 NOT IN('schemaVersion','ownerKind','ownerId','ownerRevisionId','expectedEvidenceVersion','documentId','revisionId','replacesLinkId','reason')
                OR ([key]='schemaVersion' AND (type<>2 OR value<>'1'))
                OR ([key]='replacesLinkId' AND type NOT IN(0,1)) OR ([key] NOT IN('schemaVersion','replacesLinkId') AND type<>1)
                OR ([key]='reason' AND DATALENGTH(value) NOT BETWEEN 2 AND 4000)
                OR ([key]='ownerKind' AND value COLLATE Latin1_General_100_BIN2 NOT IN('SupplierBill','SupplierPayment','PurchaseRecognition'))
                OR ([key]='expectedEvidenceVersion' AND (DATALENGTH(value)<>36 OR TRY_CONVERT(binary(8),value,1) IS NULL))
                OR ([key] IN('ownerId','ownerRevisionId','documentId','revisionId','replacesLinkId') AND type<>0
                  AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000')))
              THROW 51000,'Invalid evidence addition fields.',1;
            DECLARE @Reason nvarchar(2000)=TRIM(NCHAR(9)+NCHAR(10)+NCHAR(13)+NCHAR(32)+NCHAR(160)+NCHAR(8195) FROM JSON_VALUE(@Command,'$.reason'));
            IF LEN(@Reason)=0 THROW 51000,'A reason is required.',1;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Lock int,
              @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId')),
              @Kind varchar(32)=JSON_VALUE(@Command,'$.ownerKind'),@Owner uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.ownerId')),
              @OwnerRevision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.ownerRevisionId')),
              @Document uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.documentId')),@Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.revisionId')),
              @Replaces uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.replacesLinkId')),
              @Expected binary(8)=CONVERT(binary(8),JSON_VALUE(@Command,'$.expectedEvidenceVersion'),1),@Set uniqueidentifier,@Po uniqueidentifier,@Permission nvarchar(100),@Result nvarchar(max);
            EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Lock<0 THROW 51009,'Evidence is being changed. Retry.',1;
            SELECT @Set=Id,@Po=PurchaseOrderId,@Permission=MutationPermission FROM Accounting.FinancialEvidenceSets
              WHERE TenantId=@TenantId AND OwnerKind=@Kind AND OwnerId=@Owner AND OwnerRevisionId=@OwnerRevision;
            IF @Set IS NULL THROW 51004,'Evidence source unavailable.',1;
            IF @Permission IS NULL THROW 51003,'Source mutation authority has no authenticated provenance.',1;
            BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@Permission; END TRY
            BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current source mutation authority required.',1; THROW; END CATCH;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po AND IsDeleted=0 AND State='Ordered')
              THROW 51004,'Purchase document authority unavailable.',1;
            DECLARE @Canonical nvarchar(max)=(SELECT 1 schemaVersion,@Kind ownerKind,@Owner ownerId,@OwnerRevision ownerRevisionId,
              CONVERT(varchar(18),@Expected,1) expectedEvidenceVersion,@Document documentId,@Revision revisionId,@Replaces replacesLinkId,@Reason reason
              FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
            IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND Operation='Append' AND ActorId=@ActorId
                AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)) THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@Set AND RowVersion=@Expected)
              THROW 51009,'Evidence changed. Refresh before adding a document.',1;
            IF @Replaces IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND Id=@Replaces AND EvidenceSetId=@Set)
              THROW 51004,'Replacement predecessor is not owned by this source.',1;
            DECLARE @Link uniqueidentifier,@Addition uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME();
            EXEC Accounting.AcquireFinancialEvidenceLink @Set,@Document,@Revision,NULL,NULL,NULL,@Link OUTPUT;
            UPDATE Accounting.FinancialEvidenceSets SET Id=Id WHERE TenantId=@TenantId AND Id=@Set;
            SET @Result=(SELECT @Set evidenceSetId,@Link linkId,@Addition additionId,
              CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) evidenceVersion FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@Set FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Accounting.FinancialEvidenceReceipts(TenantId,RequestId,Operation,ActorId,EvidenceSetId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@RequestId,'Append',@ActorId,@Set,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
            INSERT Accounting.FinancialEvidenceAdditions(TenantId,Id,EvidenceSetId,LinkId,ReplacesLinkId,RequestId,ActorId,Reason,RecordedAtUtc)
              VALUES(@TenantId,@Addition,@Set,@Link,@Replaces,@RequestId,@ActorId,@Reason,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
