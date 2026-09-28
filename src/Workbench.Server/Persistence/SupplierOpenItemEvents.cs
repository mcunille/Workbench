// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemEvents
{
    // Every effect is proved from immutable recognition, bill and journal evidence here.
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
            CROSS APPLY (SELECT CASE WHEN LEFT(p.value,1)='-' THEN SUBSTRING(p.value,2,100) ELSE p.value END magnitude) n
            WHERE p.[key]='amount' AND (n.magnitude COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
              OR LEFT(n.magnitude,1)='.' OR RIGHT(n.magnitude,1)='.' OR LEN(n.magnitude)=0
              OR LEN(n.magnitude)-LEN(REPLACE(n.magnitude,'.',''))>1
              OR LEN(LEFT(n.magnitude,CHARINDEX('.',n.magnitude+'.')-1))>24
              OR LEN(SUBSTRING(n.magnitude,CHARINDEX('.',n.magnitude+'.')+1,100))>4
              OR TRY_CONVERT(decimal(28,4),p.value) IS NULL
              OR TRY_CONVERT(decimal(28,4),p.value)=0))
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
            OR EXISTS(SELECT RecognitionEventId FROM @Parsed GROUP BY RecognitionEventId HAVING COUNT(*)>1)
            THROW 51000,'Invalid or duplicate supplier event identity.',1;
          DECLARE @Event TABLE(ItemId uniqueidentifier,RecognitionEventId uniqueidentifier,JournalId uniqueidentifier,
            Ordinal int,Amount decimal(28,4),SourceEventId uniqueidentifier,SourceRevisionId uniqueidentifier,
            PurchaseOrderId uniqueidentifier,SupplierId uniqueidentifier,Currency varchar(3),PostingDate date,
            EvidenceJson nvarchar(max),AccountId uniqueidentifier,AccountVersion uniqueidentifier,
            AccountPurpose nvarchar(40),ControlAmount decimal(28,4),BillId uniqueidentifier,
            SourceId uniqueidentifier,SourceKind varchar(40),DueDate date,IsInverse bit,CorrectionId uniqueidentifier);
          INSERT @Event
          SELECT e.ItemId,e.RecognitionEventId,e.JournalId,e.Ordinal,e.Amount,j.SourceEventId,r.SourceRevision,
            u.PurchaseOrderId,u.SupplierId,u.Currency,j.PostingDate,COALESCE(br.Payload,r.EvidenceJson),
            l.AccountId,l.AccountVersion,l.AccountPurpose,l.Credit-l.Debit,b.BillId,
            COALESCE(b.BillId,r.Id),CASE WHEN b.BillId IS NULL THEN 'PurchaseRecognition' ELSE 'SupplierBill' END,
            TRY_CONVERT(date,JSON_VALUE(br.Payload,'$.dueDate')),CASE WHEN j.Id=r.JournalId THEN 0 ELSE 1 END,
            CASE WHEN j.Id=r.JournalId THEN r.CorrectionGroupId ELSE c.CorrectionGroupId END
          FROM @Parsed e
          JOIN Purchasing.RecognitionSideEvents r WITH(UPDLOCK,HOLDLOCK)
            ON r.TenantId=@TenantId AND r.Id=e.RecognitionEventId AND r.Side='Invoice'
          JOIN Purchasing.RecognitionUnits u ON u.TenantId=@TenantId AND u.Id=r.UnitId
          LEFT JOIN Purchasing.RecognitionEventCorrections c ON c.TenantId=@TenantId AND c.OriginalEventId=r.Id
          LEFT JOIN Accounting.CorrectionGroups cg ON cg.TenantId=@TenantId AND cg.Id=c.AccountingCorrectionGroupId AND cg.OriginalJournalId=r.JournalId
          LEFT JOIN Purchasing.SupplierBillPostingEvents b ON b.TenantId=@TenantId AND b.EventId=r.Id
          LEFT JOIN Purchasing.SupplierBillPostings bp ON bp.TenantId=@TenantId AND bp.BillId=b.BillId AND bp.RevisionId=r.SourceRevision
          LEFT JOIN Purchasing.SupplierBillRevisions br ON br.TenantId=@TenantId AND br.BillId=bp.BillId AND br.Id=bp.RevisionId
          JOIN Accounting.JournalEntries j ON j.TenantId=@TenantId AND j.Id=e.JournalId
            AND j.RecordedAtUtc=@RecordedAtUtc AND j.Currency=u.Currency
            AND ((j.Id=r.JournalId AND r.RecordedAtUtc=@RecordedAtUtc AND j.PostingDate=r.PostingDate)
              OR (j.Id=cg.ReversalJournalId AND cg.RecordedAtUtc=@RecordedAtUtc AND b.BillId IS NULL))
          JOIN Accounting.SourceEvents s ON s.TenantId=@TenantId AND s.Id=j.SourceEventId
            AND s.RecordedAtUtc=@RecordedAtUtc AND s.PostingDate=j.PostingDate
          JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=j.Id
            AND l.Ordinal=e.Ordinal AND l.AccountPurpose='SupplierPayable'
          WHERE (b.BillId IS NULL OR (br.Id IS NOT NULL AND r.SourceId=b.BillId
              AND EXISTS(SELECT 1 FROM Purchasing.SupplierBills bill WHERE bill.TenantId=@TenantId AND bill.Id=b.BillId
                AND bill.PurchaseOrderId=u.PurchaseOrderId AND bill.SupplierId=u.SupplierId AND bill.Currency=u.Currency)
              AND EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts receipt CROSS APPLY OPENJSON(receipt.ResultJson,'$.eventIds') ids
                WHERE receipt.TenantId=@TenantId AND receipt.RequestId=bp.RecognitionRequestId AND TRY_CONVERT(uniqueidentifier,ids.value)=r.Id)))
            AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBills owned WHERE owned.TenantId=@TenantId
              AND owned.Id=r.SourceId AND b.BillId IS NULL)
            AND ((j.Id=r.JournalId AND s.SourceKind='PurchaseRecognition' AND s.SourceId=r.Id AND s.EventKind='Invoice'
                AND s.SourceRevision=r.SourceRevision AND l.Credit=r.SourceAmount AND l.Credit>0 AND l.Debit=0)
              OR (j.Id=cg.ReversalJournalId AND s.SourceKind='PurchaseRecognition' AND s.SourceId=r.Id AND s.EventKind='Correction.Reversal'
                AND l.Debit=r.SourceAmount AND l.Debit>0 AND l.Credit=0
                AND EXISTS(SELECT 1 FROM Accounting.JournalLines old WHERE old.TenantId=@TenantId AND old.JournalId=r.JournalId
                  AND old.Ordinal=l.Ordinal AND old.AccountId=l.AccountId AND old.AccountVersion=l.AccountVersion
                  AND old.AccountPurpose=l.AccountPurpose AND old.Credit=l.Debit AND old.Debit=l.Credit)));
          IF (SELECT COUNT(*) FROM @Event)<>@Count OR EXISTS(SELECT 1 FROM @Event WHERE Amount<>ControlAmount)
            THROW 51004,'Supplier source and control amount disagree.',1;
          IF EXISTS(SELECT 1 FROM @Event e WHERE
              (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.AccountPurpose='SupplierPayable')<>1)
            OR EXISTS(SELECT ItemId FROM @Event GROUP BY ItemId HAVING COUNT(DISTINCT SourceId)>1 OR COUNT(DISTINCT SourceRevisionId)>1)
            OR EXISTS(SELECT SourceId FROM @Event WHERE IsInverse=0 GROUP BY SourceId HAVING COUNT(DISTINCT ItemId)>1)
            OR (SELECT COUNT(DISTINCT CorrectionId) FROM @Event)>1
            OR (EXISTS(SELECT 1 FROM @Event WHERE CorrectionId IS NOT NULL) AND EXISTS(SELECT 1 FROM @Event WHERE CorrectionId IS NULL))
            OR EXISTS(SELECT 1 FROM @Event e WHERE e.BillId IS NOT NULL AND
              ((SELECT COUNT(*) FROM Purchasing.SupplierBillPostingEvents b JOIN Purchasing.RecognitionSideEvents r
                 ON r.TenantId=b.TenantId AND r.Id=b.EventId WHERE b.TenantId=@TenantId AND b.BillId=e.BillId AND r.Side='Invoice' AND r.SourceAmount>0)
                <>(SELECT COUNT(*) FROM @Event other WHERE other.BillId=e.BillId)
                OR TRY_CONVERT(decimal(28,4),JSON_VALUE(e.EvidenceJson,'$.total')) IS NULL
                OR TRY_CONVERT(decimal(28,4),JSON_VALUE(e.EvidenceJson,'$.total'))<>
                   (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Event other WHERE other.BillId=e.BillId)))
            THROW 51004,'Supplier source ownership is incomplete.',1;
          IF EXISTS(SELECT 1 FROM @Event e WHERE e.IsInverse=1 AND NOT EXISTS(
              SELECT 1 FROM Purchasing.SupplierItemMovements m JOIN Purchasing.SupplierOpenItems i ON i.TenantId=m.TenantId AND i.Id=m.ItemId
              WHERE m.TenantId=@TenantId AND m.ItemId=e.ItemId AND m.RecognitionEventId=e.RecognitionEventId
                AND m.EventKind='Open' AND m.Amount=-e.Amount AND i.BillId IS NULL))
            OR EXISTS(SELECT 1 FROM @Event e JOIN Purchasing.SupplierApplications a ON a.TenantId=@TenantId
              AND (a.DebtItemId=e.ItemId OR a.FundingItemId=e.ItemId) WHERE e.IsInverse=1)
            THROW 51009,'Supplier correction requires complete unallocated source evidence.',1;
          IF EXISTS(SELECT 1 FROM @Event e JOIN Purchasing.SupplierControlAttributions a
            ON a.TenantId=@TenantId AND a.JournalId=e.JournalId AND a.Ordinal=e.Ordinal)
            OR EXISTS(SELECT JournalId,Ordinal FROM @Event GROUP BY JournalId,Ordinal HAVING COUNT(*)>1)
            THROW 51009,'Supplier control line is already attributed or repeated.',1;
          DECLARE @Expected decimal(38,4)=(SELECT SUM(CONVERT(decimal(38,4),ControlAmount)) FROM @Event),
            @Attributed decimal(38,4)=(SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Event);
          IF @Expected IS NULL OR @Expected<>@Attributed OR TRY_CONVERT(decimal(28,4),@Expected) IS NULL
            THROW 51000,'Supplier control group is not conserved.',1;
          INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc)
            SELECT TOP(1) @TenantId,@GroupId,CASE WHEN EXISTS(SELECT 1 FROM @Event WHERE CorrectionId IS NOT NULL) THEN 'CorrectSource' ELSE 'OpenRecognitionPayable' END,
              COALESCE(CorrectionId,BillId,RecognitionEventId),@RecordedAtUtc FROM @Event ORDER BY IsInverse DESC,RecognitionEventId;
          INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,
            SourceId,SourceRevisionId,BillId,SourcePostingDate,DueDate,SourceSnapshotJson,RecordedAtUtc)
            SELECT DISTINCT @TenantId,ItemId,'Payable',SupplierId,PurchaseOrderId,Currency,SourceKind,
              SourceId,SourceRevisionId,BillId,PostingDate,DueDate,EvidenceJson,@RecordedAtUtc FROM @Event WHERE IsInverse=0;
          INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) SELECT DISTINCT @TenantId,ItemId FROM @Event WHERE IsInverse=0;
          UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v JOIN @Event e ON e.ItemId=v.ItemId
            WHERE v.TenantId=@TenantId AND e.IsInverse=1;
          INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,RecognitionEventId,
            PostingDate,Amount,RecordedAtUtc)
            SELECT @TenantId,RecognitionEventId,ItemId,@GroupId,'Open',SourceEventId,RecognitionEventId,PostingDate,Amount,@RecordedAtUtc FROM @Event WHERE IsInverse=0;
          INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,RecognitionEventId,
            PostingDate,Amount,RecordedAtUtc)
            SELECT @TenantId,SourceEventId,ItemId,@GroupId,'ReverseSource',SourceEventId,RecognitionEventId,PostingDate,Amount,@RecordedAtUtc FROM @Event WHERE IsInverse=1;
          INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,
            AccountVersion,AccountPurpose,Amount)
            SELECT @TenantId,e.JournalId,@GroupId,m.Id,e.JournalId,e.Ordinal,e.AccountId,e.AccountVersion,e.AccountPurpose,e.Amount
            FROM @Event e JOIN Purchasing.SupplierItemMovements m ON m.TenantId=@TenantId
              AND m.GroupId=@GroupId AND m.ItemId=e.ItemId AND m.RecognitionEventId=e.RecognitionEventId;
        END;
        """;
}
