// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemSourceIntegration
{
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.DeriveRecognitionOpenItems
          @TenantId uniqueidentifier,@RecognitionRequestId uniqueidentifier,@BillId uniqueidentifier=NULL
        WITH EXECUTE AS OWNER
        AS
        BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @RecognitionRequestId IS NULL
            OR @TenantId='00000000-0000-0000-0000-000000000000' OR @RecognitionRequestId='00000000-0000-0000-0000-000000000000'
            OR @BillId='00000000-0000-0000-0000-000000000000'
            THROW 51000,'Invalid supplier source derivation identity.',1;
          IF APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Supplier source derivation requires the tenant accounting lock.',1;
          DECLARE @Result nvarchar(max),@Now datetimeoffset,@CorrectionId uniqueidentifier,@Kind varchar(16);
          SELECT @Result=ResultJson,@Now=RecordedAtUtc,@Kind=CommandKind FROM Purchasing.RecognitionGroupReceipts
            WHERE TenantId=@TenantId AND RequestId=@RecognitionRequestId;
          IF @Result IS NULL THROW 51004,'Stored recognition receipt is required.',1;
          SET @CorrectionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Result,'$.correctionGroupId'));
          IF @BillId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings
              WHERE TenantId=@TenantId AND BillId=@BillId AND RecognitionRequestId=@RecognitionRequestId)
            THROW 51004,'Stored bill posting ownership is required.',1;
          IF @BillId IS NULL AND EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings WHERE TenantId=@TenantId AND RecognitionRequestId=@RecognitionRequestId)
            THROW 51004,'Bill-owned recognition cannot be derived independently.',1;
          IF (@Kind='Post' AND @CorrectionId IS NOT NULL) OR (@Kind<>'Post' AND NOT EXISTS(
              SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND Id=@CorrectionId AND RecordedAtUtc=@Now))
            RETURN;
          DECLARE @Candidates TABLE(EventId uniqueidentifier PRIMARY KEY,JournalId uniqueidentifier,ItemId uniqueidentifier,Amount decimal(28,4),Inverse bit);
          INSERT @Candidates
            SELECT r.Id,r.JournalId,COALESCE(@BillId,r.Id),r.SourceAmount,0
            FROM OPENJSON(@Result,'$.eventIds') ids JOIN Purchasing.RecognitionSideEvents r
              ON r.TenantId=@TenantId AND r.Id=TRY_CONVERT(uniqueidentifier,ids.value)
            WHERE r.Side='Invoice' AND r.SourceAmount>0 AND r.RecordedAtUtc=@Now;
          IF EXISTS(SELECT 1 FROM @Candidates c JOIN Purchasing.RecognitionSideEvents r ON r.TenantId=@TenantId AND r.Id=c.EventId
              WHERE (@BillId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostingEvents b
                WHERE b.TenantId=@TenantId AND b.BillId=@BillId AND b.EventId=r.Id))
                OR (@BillId IS NULL AND EXISTS(SELECT 1 FROM Purchasing.SupplierBills b
                  WHERE b.TenantId=@TenantId AND b.Id=r.SourceId))) RETURN;
          IF @BillId IS NOT NULL AND (NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings p
              JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=p.TenantId AND revision.BillId=p.BillId AND revision.Id=p.RevisionId
              WHERE p.TenantId=@TenantId AND p.BillId=@BillId AND p.RecordedAtUtc=@Now
                AND TRY_CONVERT(decimal(28,4),JSON_VALUE(revision.Payload,'$.total'))=(SELECT COALESCE(SUM(CONVERT(decimal(38,4),Amount)),0) FROM @Candidates))
            OR EXISTS(SELECT 1 FROM @Candidates c JOIN Purchasing.RecognitionSideEvents r ON r.TenantId=@TenantId AND r.Id=c.EventId
              JOIN Purchasing.RecognitionUnits u ON u.TenantId=r.TenantId AND u.Id=r.UnitId
              WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings p JOIN Purchasing.SupplierBills b ON b.TenantId=p.TenantId AND b.Id=p.BillId
                WHERE p.TenantId=@TenantId AND p.BillId=@BillId AND p.RevisionId=r.SourceRevision AND r.SourceId=b.Id
                  AND b.SupplierId=u.SupplierId AND b.PurchaseOrderId=u.PurchaseOrderId AND b.Currency=u.Currency))) RETURN;
          IF @CorrectionId IS NOT NULL
          BEGIN
            INSERT @Candidates
              SELECT r.Id,g.ReversalJournalId,m.ItemId,-r.SourceAmount,1
              FROM Purchasing.RecognitionEventCorrections c JOIN Purchasing.RecognitionSideEvents r ON r.TenantId=c.TenantId AND r.Id=c.OriginalEventId
              JOIN Accounting.CorrectionGroups g ON g.TenantId=c.TenantId AND g.Id=c.AccountingCorrectionGroupId AND g.OriginalJournalId=r.JournalId
              JOIN Purchasing.SupplierItemMovements m ON m.TenantId=r.TenantId AND m.RecognitionEventId=r.Id AND m.EventKind='Open'
              WHERE c.TenantId=@TenantId AND c.CorrectionGroupId=@CorrectionId AND r.Side='Invoice' AND r.SourceAmount>0;
            -- An unsupported historical original remains a visible reconciliation discrepancy, never invented capacity.
            IF (SELECT COUNT(*) FROM @Candidates WHERE Inverse=1)<>(SELECT COUNT(*) FROM Purchasing.RecognitionEventCorrections c
              JOIN Purchasing.RecognitionSideEvents r ON r.TenantId=c.TenantId AND r.Id=c.OriginalEventId
              WHERE c.TenantId=@TenantId AND c.CorrectionGroupId=@CorrectionId AND r.Side='Invoice' AND r.SourceAmount>0) RETURN;
          END;
          IF NOT EXISTS(SELECT 1 FROM @Candidates) RETURN;
          -- Unknown/invalid legacy control evidence is retained, but cannot mint an item to conceal the discrepancy.
          IF EXISTS(SELECT 1 FROM @Candidates c WHERE (SELECT COUNT(*) FROM Accounting.JournalLines l
              WHERE l.TenantId=@TenantId AND l.JournalId=c.JournalId AND l.AccountPurpose='SupplierPayable')<>1
            OR NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId
              JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
              JOIN Purchasing.RecognitionSideEvents r ON r.TenantId=@TenantId AND r.Id=c.EventId
              JOIN Purchasing.RecognitionUnits u ON u.TenantId=r.TenantId AND u.Id=r.UnitId
              WHERE l.TenantId=@TenantId AND l.JournalId=c.JournalId AND l.AccountPurpose='SupplierPayable'
                AND l.Credit-l.Debit=c.Amount AND j.RecordedAtUtc=@Now AND s.RecordedAtUtc=@Now
                AND s.PostingDate=j.PostingDate AND j.Currency=u.Currency AND s.SourceKind='PurchaseRecognition' AND s.SourceId=c.EventId
                AND ((c.Inverse=0 AND s.EventKind='Invoice' AND s.SourceRevision=r.SourceRevision AND j.PostingDate=r.PostingDate
                    AND l.Debit=0 AND l.Credit=r.SourceAmount)
                  OR (c.Inverse=1 AND s.EventKind='Correction.Reversal' AND l.Credit=0 AND l.Debit=r.SourceAmount
                    AND EXISTS(SELECT 1 FROM Accounting.JournalLines old WHERE old.TenantId=@TenantId AND old.JournalId=r.JournalId
                      AND old.Ordinal=l.Ordinal AND old.AccountId=l.AccountId AND old.AccountVersion=l.AccountVersion
                      AND old.AccountPurpose=l.AccountPurpose AND old.Credit=l.Debit AND old.Debit=l.Credit))))) RETURN;
          IF EXISTS(SELECT 1 FROM @Candidates c JOIN Purchasing.SupplierItemMovements m ON m.TenantId=@TenantId
              AND m.RecognitionEventId=c.EventId AND m.EventKind=CASE WHEN c.Inverse=1 THEN 'ReverseSource' ELSE 'Open' END)
          BEGIN
            IF EXISTS(SELECT 1 FROM @Candidates c WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements m
              WHERE m.TenantId=@TenantId AND m.RecognitionEventId=c.EventId AND m.ItemId=c.ItemId AND m.Amount=c.Amount
                AND m.EventKind=CASE WHEN c.Inverse=1 THEN 'ReverseSource' ELSE 'Open' END))
              THROW 51009,'Supplier source derivation is incomplete or already owned.',1;
            RETURN;
          END;
          DECLARE @Events nvarchar(max)=(SELECT c.ItemId itemId,c.EventId recognitionEventId,c.JournalId journalId,
              l.Ordinal ordinal,CONVERT(varchar(60),c.Amount) amount
            FROM @Candidates c JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=c.JournalId AND l.AccountPurpose='SupplierPayable'
            ORDER BY c.EventId FOR JSON PATH);
          EXEC Purchasing.AppendSupplierEventGroup @TenantId,@RecognitionRequestId,@Now,@Events;
        END;
        """;

    internal const string HooksSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PostRecognition')),
          @Anchor nvarchar(max)=N'INSERT Security.TenantSecurityAuditEvents';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 OR CHARINDEX(N'@SuppressResult bit=0',@Definition)=0
          THROW 50020,'Unsupported supplier recognition integration predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,@Anchor,N'
          IF NOT EXISTS(SELECT 1 FROM @Events posted JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=@TenantId AND e.Id=posted.Id
            JOIN Purchasing.SupplierBillRevisions b ON b.TenantId=e.TenantId AND b.BillId=e.SourceId AND b.Id=e.SourceRevision)
            EXEC Purchasing.DeriveRecognitionOpenItems @TenantId,@RequestId;
          '+@Anchor);
        EXEC sys.sp_executesql @Definition;

        SET @Definition=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PostSupplierBill'));
        SET @Anchor=N'INSERT Purchasing.SupplierBillReceipts';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 OR CHARINDEX(N'INSERT Purchasing.SupplierBillPostingEvents',@Definition)=0
          THROW 50020,'Unsupported supplier bill integration predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,@Anchor,N'EXEC Purchasing.DeriveRecognitionOpenItems @TenantId,@KernelRequest,@BillId; '+@Anchor);
        EXEC sys.sp_executesql @Definition;

        SET @Definition=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.CorrectRecognition'));
        SET @Anchor=N'DECLARE @Originals TABLE(';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 OR CHARINDEX(N'Bill-owned recognition requires',@Definition)=0
          THROW 50020,'Unsupported supplier recognition correction predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,@Anchor,N'
          IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents e JOIN Purchasing.SupplierItemMovements m
              ON m.TenantId=e.TenantId AND m.RecognitionEventId=e.Id
            JOIN Purchasing.SupplierApplications a ON a.TenantId=m.TenantId AND (a.DebtItemId=m.ItemId OR a.FundingItemId=m.ItemId)
            WHERE e.TenantId=@TenantId AND e.UnitId=@UnitId)
            THROW 51009,''Supplier applications require their complete source correction adapter.'',1;
          '+@Anchor);
        SET @Definition=REPLACE(@Definition,N'INSERT Security.TenantSecurityAuditEvents',
          N'EXEC Purchasing.DeriveRecognitionOpenItems @TenantId,@RequestId; INSERT Security.TenantSecurityAuditEvents');
        EXEC sys.sp_executesql @Definition;
        """;
}
