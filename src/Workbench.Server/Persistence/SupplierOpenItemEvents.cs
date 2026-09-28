// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemEvents
{
    // Task 1 admits only opening payable evidence from posted invoice-side recognition.
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.AppendSupplierEventGroup
          @TenantId uniqueidentifier,@GroupId uniqueidentifier,@RecordedAtUtc datetimeoffset,@Events nvarchar(max)
        WITH EXECUTE AS OWNER
        AS
        BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @GroupId IS NULL OR @RecordedAtUtc IS NULL
            OR @TenantId='00000000-0000-0000-0000-000000000000'
            OR @GroupId='00000000-0000-0000-0000-000000000000'
            OR @Events IS NULL OR DATALENGTH(@Events)>262144 OR ISJSON(@Events,ARRAY)<>1
            THROW 51000,'Invalid supplier event group envelope.',1;
          IF APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Supplier event group requires the tenant accounting lock.',1;
          DECLARE @Count int=(SELECT COUNT(*) FROM OPENJSON(@Events));
          IF @Count<1 OR @Count>1000 OR EXISTS(SELECT 1 FROM OPENJSON(@Events) WHERE [type]<>5)
            THROW 51000,'Invalid supplier event count or shape.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p
              GROUP BY e.[key],p.[key] HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p
              WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN
                ('itemId','recognitionEventId','journalId','ordinal','amount')
                OR (p.[key] IN ('itemId','recognitionEventId','journalId','amount') AND p.[type]<>1)
                OR (p.[key]='ordinal' AND p.[type]<>2))
            OR EXISTS(SELECT 1 FROM OPENJSON(@Events) e WHERE (SELECT COUNT(*) FROM OPENJSON(e.value))<>5)
            THROW 51000,'Invalid supplier event properties.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p
            WHERE p.[key]='amount' AND (p.value COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
              OR LEFT(p.value,1)='.' OR RIGHT(p.value,1)='.' OR LEN(p.value)=0
              OR LEN(p.value)-LEN(REPLACE(p.value,'.',''))>1
              OR LEN(LEFT(p.value,CHARINDEX('.',p.value+'.')-1))>24
              OR LEN(SUBSTRING(p.value,CHARINDEX('.',p.value+'.')+1,100))>4
              OR TRY_CONVERT(decimal(28,4),p.value) IS NULL
              OR TRY_CONVERT(decimal(28,4),p.value)<=0))
            THROW 51000,'Invalid exact supplier event amount.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p
            WHERE p.[key] IN ('itemId','recognitionEventId','journalId')
              AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL
                OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
            THROW 51000,'Invalid supplier event identity.',1;
          DECLARE @Parsed TABLE(ItemId uniqueidentifier,RecognitionEventId uniqueidentifier,
            JournalId uniqueidentifier,Ordinal int,Amount decimal(28,4));
          INSERT @Parsed
          SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.itemId')),
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.recognitionEventId')),
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId')),
            TRY_CONVERT(int,JSON_VALUE(e.value,'$.ordinal')),
            TRY_CONVERT(decimal(28,4),JSON_VALUE(e.value,'$.amount'))
          FROM OPENJSON(@Events) e;
          IF EXISTS(SELECT 1 FROM @Parsed WHERE ItemId IS NULL OR ItemId='00000000-0000-0000-0000-000000000000'
            OR RecognitionEventId IS NULL OR RecognitionEventId='00000000-0000-0000-0000-000000000000'
            OR JournalId IS NULL OR JournalId='00000000-0000-0000-0000-000000000000'
            OR Ordinal IS NULL OR Ordinal NOT BETWEEN 1 AND 1000 OR Amount IS NULL)
            OR EXISTS(SELECT ItemId FROM @Parsed GROUP BY ItemId HAVING COUNT(*)>1)
            OR EXISTS(SELECT RecognitionEventId FROM @Parsed GROUP BY RecognitionEventId HAVING COUNT(*)>1)
            THROW 51000,'Invalid or duplicate supplier event identity.',1;
          DECLARE @Event TABLE(ItemId uniqueidentifier,RecognitionEventId uniqueidentifier,JournalId uniqueidentifier,
            Ordinal int,Amount decimal(28,4),SourceEventId uniqueidentifier,SourceRevisionId uniqueidentifier,
            PurchaseOrderId uniqueidentifier,SupplierId uniqueidentifier,Currency varchar(3),PostingDate date,
            EvidenceJson nvarchar(max),AccountId uniqueidentifier,AccountVersion uniqueidentifier,
            AccountPurpose nvarchar(40),ControlAmount decimal(28,4));
          INSERT @Event
          SELECT e.ItemId,e.RecognitionEventId,e.JournalId,e.Ordinal,e.Amount,j.SourceEventId,r.SourceRevision,
            u.PurchaseOrderId,u.SupplierId,u.Currency,r.PostingDate,r.EvidenceJson,
            l.AccountId,l.AccountVersion,l.AccountPurpose,l.Credit-l.Debit
          FROM @Parsed e
          JOIN Purchasing.RecognitionSideEvents r WITH(UPDLOCK,HOLDLOCK)
            ON r.TenantId=@TenantId AND r.Id=e.RecognitionEventId AND r.Side='Invoice'
              AND r.JournalId=e.JournalId AND r.RecordedAtUtc=@RecordedAtUtc
          JOIN Purchasing.RecognitionUnits u ON u.TenantId=@TenantId AND u.Id=r.UnitId
          JOIN Accounting.JournalEntries j ON j.TenantId=@TenantId AND j.Id=e.JournalId
            AND j.RecordedAtUtc=@RecordedAtUtc AND j.PostingDate=r.PostingDate
          JOIN Accounting.SourceEvents s ON s.TenantId=@TenantId AND s.Id=j.SourceEventId
            AND s.SourceKind='PurchaseRecognition' AND s.SourceId=r.Id AND s.EventKind='Invoice'
            AND s.RecordedAtUtc=@RecordedAtUtc
          JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=j.Id
            AND l.Ordinal=e.Ordinal AND l.AccountPurpose='SupplierPayable' AND l.Credit>0 AND l.Debit=0
            AND l.Credit=r.SourceAmount;
          IF (SELECT COUNT(*) FROM @Event)<>@Count OR EXISTS(SELECT 1 FROM @Event WHERE Amount<>ControlAmount)
            THROW 51004,'Supplier source and control amount disagree.',1;
          IF EXISTS(SELECT 1 FROM @Event e JOIN Purchasing.SupplierControlAttributions a
            ON a.TenantId=@TenantId AND a.JournalId=e.JournalId AND a.Ordinal=e.Ordinal)
            OR EXISTS(SELECT JournalId,Ordinal FROM @Event GROUP BY JournalId,Ordinal HAVING COUNT(*)>1)
            THROW 51009,'Supplier control line is already attributed or repeated.',1;
          DECLARE @Expected decimal(38,4)=(SELECT SUM(CONVERT(decimal(38,4),ControlAmount)) FROM @Event),
            @Attributed decimal(38,4)=(SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Event);
          IF @Expected IS NULL OR @Expected<>@Attributed OR TRY_CONVERT(decimal(28,4),@Expected) IS NULL
            THROW 51000,'Supplier control group is not conserved.',1;
          INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc)
            SELECT TOP(1) @TenantId,@GroupId,'OpenRecognitionPayable',RecognitionEventId,@RecordedAtUtc FROM @Event ORDER BY RecognitionEventId;
          INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,
            SourceId,SourceRevisionId,BillId,SourcePostingDate,DueDate,SourceSnapshotJson,RecordedAtUtc)
            SELECT @TenantId,ItemId,'Payable',SupplierId,PurchaseOrderId,Currency,'PurchaseRecognition',
              RecognitionEventId,SourceRevisionId,NULL,PostingDate,NULL,EvidenceJson,@RecordedAtUtc FROM @Event;
          INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) SELECT @TenantId,ItemId FROM @Event;
          INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,RecognitionEventId,
            PostingDate,Amount,RecordedAtUtc)
            SELECT @TenantId,NEWID(),ItemId,@GroupId,'Open',SourceEventId,RecognitionEventId,PostingDate,Amount,@RecordedAtUtc FROM @Event;
          INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,
            AccountVersion,AccountPurpose,Amount)
            SELECT @TenantId,NEWID(),@GroupId,m.Id,e.JournalId,e.Ordinal,e.AccountId,e.AccountVersion,e.AccountPurpose,e.Amount
            FROM @Event e JOIN Purchasing.SupplierItemMovements m ON m.TenantId=@TenantId
              AND m.GroupId=@GroupId AND m.ItemId=e.ItemId;
        END;
        """;
}
