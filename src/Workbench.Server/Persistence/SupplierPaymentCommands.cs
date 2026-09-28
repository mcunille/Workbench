// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierPaymentCommands
{
    internal const string SupplierGuardSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SavePurchaseOrder')),
          @Anchor nvarchar(max)=N'EXEC Purchasing.ValidatePurchaseOrderContent @Draft,@TenantId,@TargetId,@Calculation OUTPUT;';
        IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 THROW 50020,'Unsupported payment purchase guard predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,@Anchor,N'
          IF @Operation=''Amend'' AND EXISTS(SELECT 1 FROM Purchasing.SupplierPayments WHERE TenantId=@TenantId AND PurchaseOrderId=@TargetId
            AND (TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId'')) IS NULL OR SupplierId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId''))))
            THROW 50415,''Supplier is fixed by immutable payment history.'',1;
          '+@Anchor);
        EXEC sys.sp_executesql @Definition;
        """;

    internal const string ControlSql = """
        CREATE FUNCTION Purchasing.SupplierPaymentControl(@TenantId uniqueidentifier,@ItemId uniqueidentifier)
        RETURNS TABLE AS RETURN
          SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.id')) AccountId,
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.version')) AccountVersion,
            CONVERT(nvarchar(32),JSON_VALUE(p.EvidenceJson,'$.advanceAccount.code')) COLLATE Latin1_General_100_BIN2 AccountCode,
            CONVERT(nvarchar(160),JSON_VALUE(p.EvidenceJson,'$.advanceAccount.name')) AccountName,
            CONVERT(nvarchar(40),JSON_VALUE(p.EvidenceJson,'$.advanceAccount.type')) AccountType,
            CONVERT(nvarchar(40),JSON_VALUE(p.EvidenceJson,'$.advanceAccount.purpose')) AccountPurpose
          FROM Purchasing.SupplierPayments p
          JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=p.TenantId AND g.Id=p.GroupId AND g.Operation='RecordPayment' AND g.SourceId=p.Id AND g.RecordedAtUtc=p.RecordedAtUtc
          JOIN Purchasing.SupplierOpenItems i ON i.TenantId=p.TenantId AND i.Id=p.Id AND i.Kind='Advance' AND i.SourceKind='SupplierPayment'
            AND i.SourceId=p.Id AND i.SourceRevisionId=p.RevisionId AND i.SourcePostingDate=p.PostingDate AND i.RecordedAtUtc=p.RecordedAtUtc
            AND i.SupplierId=p.SupplierId AND i.PurchaseOrderId=p.PurchaseOrderId AND i.Currency=p.Currency
            AND CONVERT(varbinary(max),i.SourceSnapshotJson)=CONVERT(varbinary(max),p.EvidenceJson)
          JOIN Accounting.SourceEvents s ON s.TenantId=p.TenantId AND s.SourceKind='SupplierPayment' AND s.SourceId=p.Id AND s.SourceRevision=p.RevisionId
            AND s.EventKind='Payment' AND s.PostingDate=p.PostingDate AND s.DocumentDate=p.PaymentDate AND s.EffectiveDate=p.EffectiveDate AND s.ActorId=p.ActorId
            AND s.RecordedAtUtc=p.RecordedAtUtc AND CONVERT(varbinary(max),s.SnapshotJson)=CONVERT(varbinary(max),p.EvidenceJson)
          JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.Currency=p.Currency AND j.PostingDate=p.PostingDate AND j.RecordedAtUtc=p.RecordedAtUtc
          JOIN Accounting.PostingReceipts r ON r.TenantId=s.TenantId AND r.SourceEventId=s.Id AND r.JournalId=j.Id AND r.SourceCommandKind='Supplier.Payment'
            AND r.ActorId=p.ActorId AND r.RecordedAtUtc=p.RecordedAtUtc AND CONVERT(varbinary(max),r.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(p.EvidenceJson,'$.command'))
          CROSS APPLY(SELECT COALESCE(SUM(CONVERT(decimal(38,4),a.Amount)),0) Amount,COUNT(*) Count FROM Purchasing.SupplierApplications a WHERE a.TenantId=p.TenantId AND a.GroupId=p.GroupId) applied
          WHERE p.TenantId=@TenantId AND p.Id=@ItemId
            AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.groupId'))=p.GroupId
            AND TRY_CONVERT(decimal(28,4),JSON_VALUE(p.EvidenceJson,'$.command.amount'))=p.Amount AND applied.Amount<=p.Amount
            AND JSON_VALUE(p.EvidenceJson,'$.advanceAccount.type')='Asset' AND JSON_VALUE(p.EvidenceJson,'$.advanceAccount.purpose')='SupplierAdvance'
            AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.id')) IS NOT NULL
            AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.version')) IS NOT NULL
            AND applied.Count=(SELECT COUNT(*) FROM OPENJSON(p.EvidenceJson,'$.allocations'))
            AND NOT EXISTS(SELECT 1 FROM OPENJSON(p.EvidenceJson,'$.allocations') expected WHERE NOT EXISTS(
              SELECT 1 FROM Purchasing.SupplierApplications a WHERE a.TenantId=p.TenantId AND a.GroupId=p.GroupId
                AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.applicationId')) AND a.FundingItemId=p.Id
                AND a.DebtItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.itemId'))
                AND a.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(expected.value,'$.amount')) AND a.PostingDate=p.PostingDate AND a.RecordedAtUtc=p.RecordedAtUtc AND a.ActorId=p.ActorId))
            AND (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=p.TenantId AND m.GroupId=p.GroupId)=1+2*applied.Count
            AND (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=p.TenantId AND m.GroupId=p.GroupId AND m.ItemId=p.Id
              AND m.EventKind='Open' AND m.Amount=p.Amount AND m.SourceEventId=s.Id AND m.PostingDate=p.PostingDate AND m.RecordedAtUtc=p.RecordedAtUtc)=1
            AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=p.TenantId AND m.GroupId=p.GroupId
              AND (m.SourceEventId<>s.Id OR m.PostingDate<>p.PostingDate OR m.RecordedAtUtc<>p.RecordedAtUtc))
            AND NOT EXISTS(SELECT a.DebtItemId,-SUM(CONVERT(decimal(38,4),a.Amount)) Amount FROM Purchasing.SupplierApplications a WHERE a.TenantId=p.TenantId AND a.GroupId=p.GroupId GROUP BY a.DebtItemId
              EXCEPT SELECT m.ItemId,SUM(CONVERT(decimal(38,4),m.Amount)) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=p.TenantId AND m.GroupId=p.GroupId AND m.ItemId<>p.Id AND m.EventKind='Apply' GROUP BY m.ItemId)
            AND COALESCE((SELECT SUM(CONVERT(decimal(38,4),m.Amount)) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=p.TenantId AND m.GroupId=p.GroupId AND m.ItemId=p.Id AND m.EventKind='Apply'),0)=-applied.Amount
            AND (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id AND l.Credit>0)=1
            AND EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id AND l.AccountId=p.FundingAccountId
              AND l.AccountVersion=p.FundingAccountVersion AND l.AccountPurpose=p.FundingAccountPurpose AND l.AccountPurpose IN('Bank','Cash')
              AND l.AccountCode=JSON_VALUE(p.EvidenceJson,'$.fundingAccount.code') COLLATE Latin1_General_100_BIN2 AND l.AccountName=JSON_VALUE(p.EvidenceJson,'$.fundingAccount.name') AND l.Credit=p.Amount AND l.Debit=0)
            AND COALESCE((SELECT SUM(CONVERT(decimal(38,4),l.Debit)-l.Credit) FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id AND l.AccountPurpose='SupplierAdvance'),0)=p.Amount-applied.Amount
            AND NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id AND l.AccountPurpose='SupplierAdvance'
              AND (l.AccountId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.id')) OR l.AccountVersion<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.EvidenceJson,'$.advanceAccount.version'))
                OR l.AccountCode<>JSON_VALUE(p.EvidenceJson,'$.advanceAccount.code') COLLATE Latin1_General_100_BIN2 OR l.AccountName<>JSON_VALUE(p.EvidenceJson,'$.advanceAccount.name')))
            AND NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id
              AND l.AccountPurpose NOT IN('Bank','Cash','SupplierAdvance','SupplierPayable'))
            AND NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=p.TenantId AND l.JournalId=j.Id AND l.AccountPurpose IN('SupplierAdvance','SupplierPayable')
              AND COALESCE((SELECT SUM(CONVERT(decimal(38,4),a.Amount)) FROM Purchasing.SupplierControlAttributions a WHERE a.TenantId=p.TenantId AND a.GroupId=p.GroupId AND a.JournalId=j.Id AND a.Ordinal=l.Ordinal
                AND a.AccountId=l.AccountId AND a.AccountVersion=l.AccountVersion AND a.AccountPurpose=l.AccountPurpose),0)<>
                CASE l.AccountPurpose WHEN 'SupplierPayable' THEN l.Credit-l.Debit ELSE l.Debit-l.Credit END)
            AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.TenantId=a.TenantId AND m.Id=a.MovementId
              WHERE a.TenantId=p.TenantId AND a.GroupId=p.GroupId AND (a.JournalId<>j.Id OR m.GroupId<>p.GroupId
                OR (m.ItemId<>p.Id AND (m.EventKind<>'Apply' OR m.Amount<>a.Amount))
                OR (m.ItemId=p.Id AND (m.EventKind<>'Open' OR a.Amount<>p.Amount-applied.Amount))));
        """;

    internal const string KernelSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.PostJournal'));
        IF @Definition IS NULL OR CHARINDEX(N'IF @SupplierControlItems IS NOT NULL',@Definition)=0
          THROW 50020,'Unsupported payment journal predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,N'IF @SupplierControlItems IS NOT NULL',N'IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind<>N''Supplier.Payment''');
        SET @Definition=REPLACE(@Definition,N'IF EXISTS(SELECT 1 FROM @Input i LEFT JOIN Accounting.Accounts',N'
          IF @SupplierControlItems IS NOT NULL AND @SourceCommandKind=N''Supplier.Payment''
          BEGIN
            IF @SourceKind<>N''SupplierPayment'' OR @EventKind<>N''Payment'' OR @RequiredPermission<>N''SupplierPaymentsRecord''
              OR ISJSON(@SupplierControlItems,ARRAY)<>1 OR DATALENGTH(@SupplierControlItems)>262144
              OR APPLOCK_MODE(''public'',N''Accounting:''+CONVERT(nvarchar(36),@TenantId),''Transaction'')<>''Exclusive''
              THROW 51004,''Invalid payment historical control authority.'',1;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N''SupplierPaymentsRecord'';
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N''SupplierAllocationsManage'';
            IF EXISTS(SELECT 1 FROM OPENJSON(@SupplierControlItems) e WHERE e.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
                OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN(''ordinal'',''itemId'')))
              OR EXISTS(SELECT JSON_VALUE(value,''$.itemId'') FROM OPENJSON(@SupplierControlItems) GROUP BY JSON_VALUE(value,''$.itemId'') HAVING COUNT(*)>1)
              OR (SELECT COUNT(*) FROM OPENJSON(@SupplierControlItems))<>(SELECT COUNT(*) FROM OPENJSON(@SourceSnapshot,''$.allocations''))
              THROW 51004,''Invalid payment control references.'',1;
            DECLARE @PaymentProof TABLE(Ordinal int,ItemId uniqueidentifier,Amount decimal(28,4),AccountId uniqueidentifier,AccountVersion uniqueidentifier,
              AccountCode nvarchar(32),AccountName nvarchar(160),AccountType nvarchar(40),AccountPurpose nvarchar(40));
            INSERT @PaymentProof SELECT line.Ordinal,item.Id,TRY_CONVERT(decimal(28,4),JSON_VALUE(a.value,''$.amount'')),
              h.AccountId,h.AccountVersion,h.AccountCode,h.AccountName,h.AccountType,h.AccountPurpose
              FROM OPENJSON(@SupplierControlItems) e
              JOIN OPENJSON(@SourceSnapshot,''$.allocations'') a ON JSON_VALUE(a.value,''$.itemId'')=JSON_VALUE(e.value,''$.itemId'')
                AND JSON_VALUE(a.value,''$.ordinal'')=JSON_VALUE(e.value,''$.ordinal'')
              JOIN Purchasing.SupplierOpenItems item ON item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,''$.itemId''))
              JOIN Purchasing.SupplierBillPostings b ON b.TenantId=item.TenantId AND b.BillId=item.BillId AND b.RevisionId=item.SourceRevisionId
              CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) h
              JOIN @Input line ON line.Ordinal=TRY_CONVERT(int,JSON_VALUE(e.value,''$.ordinal'')) AND line.AccountId=h.AccountId AND line.AccountVersion=h.AccountVersion
              WHERE item.Kind=''Payable'' AND item.SourceKind=''SupplierBill'' AND item.SourceId=item.BillId
                AND item.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,''$.billId'')) AND item.Currency=@Currency AND item.SourcePostingDate<=@PostingDate
                AND item.SupplierId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.command.supplierId''))
                AND item.PurchaseOrderId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,''$.command.purchaseOrderId''))
                AND line.Debit>0 AND line.Credit=0 AND h.AccountPurpose=''SupplierPayable'' AND h.AccountType=''Liability'';
            IF (SELECT COUNT(*) FROM @PaymentProof)<>(SELECT COUNT(*) FROM OPENJSON(@SupplierControlItems))
              OR EXISTS(SELECT 1 FROM @PaymentProof WHERE Amount IS NULL OR Amount<=0)
              OR EXISTS(SELECT 1 FROM @Input l JOIN @PaymentProof p ON p.Ordinal=l.Ordinal GROUP BY l.Ordinal,l.Debit HAVING SUM(CONVERT(decimal(38,4),p.Amount))<>l.Debit)
              THROW 51004,''Payment historical control evidence is unavailable.'',1;
            INSERT @Historical SELECT DISTINCT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose FROM @PaymentProof;
          END;
          IF EXISTS(SELECT 1 FROM @Input i LEFT JOIN Accounting.Accounts');
        EXEC sys.sp_executesql @Definition;
        """;

    // This is part of AppendSupplierEventGroup: it owns the same event persistence
    // boundary as recognition and applications, using journal identities only.
    internal const string EventsSql = """
          IF JSON_VALUE(@Events,'$[0].paymentId') IS NOT NULL
          BEGIN
            IF @Count<>1 OR (SELECT COUNT(*) FROM OPENJSON(JSON_QUERY(@Events,'$[0]')))<>2
              OR EXISTS(SELECT 1 FROM OPENJSON(JSON_QUERY(@Events,'$[0]')) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('paymentId','journalId')
                OR type<>1 OR DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL)
              THROW 51000,'Invalid supplier payment event identity.',1;
            DECLARE @Pay uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].paymentId')),
              @PayJournal uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].journalId')),
              @PaySource uniqueidentifier,@PayRevision uniqueidentifier,@PaySnapshot nvarchar(max),@PayActor uniqueidentifier,
              @PayCurrency varchar(3),@PayDate date,@PayPaymentDate date,@PayEffective date;
            SELECT @PaySource=s.Id,@PayRevision=s.SourceRevision,@PaySnapshot=s.SnapshotJson,@PayActor=s.ActorId,
              @PayCurrency=j.Currency,@PayDate=j.PostingDate,@PayPaymentDate=s.DocumentDate,@PayEffective=s.EffectiveDate
              FROM Accounting.SourceEvents s JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.Id=@PayJournal
                AND j.RecordedAtUtc=@RecordedAtUtc AND j.PostingDate=s.PostingDate AND j.DocumentDate=s.DocumentDate AND j.EffectiveDate=s.EffectiveDate
              JOIN Accounting.PostingReceipts r ON r.TenantId=s.TenantId AND r.SourceEventId=s.Id AND r.JournalId=j.Id
                AND r.SourceCommandKind='Supplier.Payment' AND r.ActorId=s.ActorId AND r.RecordedAtUtc=@RecordedAtUtc
              WHERE s.TenantId=@TenantId AND s.SourceKind='SupplierPayment' AND s.SourceId=@Pay AND s.EventKind='Payment' AND s.RecordedAtUtc=@RecordedAtUtc
                AND CONVERT(varbinary(max),r.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(s.SnapshotJson,'$.command'));
            IF @PaySource IS NULL OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.groupId'))<>@GroupId
              OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.paymentId'))<>@Pay
              OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.paymentRevisionId'))<>@PayRevision
              OR JSON_VALUE(@PaySnapshot,'$.command.operation')<>'RecordSupplierPayment'
              THROW 51004,'Authentic supplier payment source is unavailable.',1;
            DECLARE @PayAmount decimal(28,4)=TRY_CONVERT(decimal(28,4),JSON_VALUE(@PaySnapshot,'$.command.amount')),
              @PaySupplier uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.supplierId')),
              @PayPo uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.purchaseOrderId')),
              @PayFunding uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.id')),
              @PayAdvance uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.id'));
            IF @PayAmount IS NULL OR @PayAmount<=0 OR @PaySupplier IS NULL OR @PayPo IS NULL
              OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts a WHERE a.TenantId=@TenantId AND a.Id=@PayAdvance AND a.ArchivedAtUtc IS NULL
                AND a.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.version')) AND a.Type='Asset' AND a.Purpose='SupplierAdvance'
                AND a.Code=JSON_VALUE(@PaySnapshot,'$.advanceAccount.code') AND a.Name=JSON_VALUE(@PaySnapshot,'$.advanceAccount.name')
                AND JSON_VALUE(@PaySnapshot,'$.advanceAccount.type')=a.Type AND JSON_VALUE(@PaySnapshot,'$.advanceAccount.purpose')=a.Purpose)
              OR NOT EXISTS(SELECT 1 FROM Accounting.Configurations c CROSS APPLY OPENJSON(c.Payload,'$.mappings') m WHERE c.TenantId=@TenantId
                AND c.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.expectedConfigurationVersion'))
                AND JSON_VALUE(m.value,'$.slot')='SupplierAdvance' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))=@PayAdvance)
              OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts a WHERE a.TenantId=@TenantId AND a.Id=@PayFunding AND a.ArchivedAtUtc IS NULL
                AND a.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')) AND a.Type='Asset' AND a.Purpose IN('Bank','Cash')
                AND a.Code=JSON_VALUE(@PaySnapshot,'$.fundingAccount.code') AND a.Name=JSON_VALUE(@PaySnapshot,'$.fundingAccount.name'))
              THROW 51004,'Payment account source snapshots are unavailable.',1;
            DECLARE @PayApplications TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier UNIQUE,BillId uniqueidentifier,Amount decimal(28,4),Ordinal int,
              AccountId uniqueidentifier,AccountVersion uniqueidentifier);
            INSERT @PayApplications SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.applicationId')),
              TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.billId')),
              TRY_CONVERT(decimal(28,4),JSON_VALUE(a.value,'$.amount')),TRY_CONVERT(int,JSON_VALUE(a.value,'$.ordinal')),c.AccountId,c.AccountVersion
              FROM OPENJSON(@PaySnapshot,'$.allocations') a CROSS APPLY Purchasing.SupplierItemControl(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId'))) c;
            IF (SELECT COUNT(*) FROM @PayApplications)<>(SELECT COUNT(*) FROM OPENJSON(@PaySnapshot,'$.command.allocations'))
              OR EXISTS(SELECT 1 FROM @PayApplications a WHERE Amount IS NULL OR Amount<=0 OR Ordinal IS NULL OR NOT EXISTS(
                SELECT 1 FROM OPENJSON(@PaySnapshot,'$.command.allocations') original
                JOIN Purchasing.SupplierOpenItems i ON i.TenantId=@TenantId AND i.Id=a.ItemId AND i.Kind='Payable' AND i.BillId=a.BillId AND i.SourceKind='SupplierBill'
                  AND i.SourceId=i.BillId AND i.SupplierId=@PaySupplier AND i.PurchaseOrderId=@PayPo AND i.Currency=@PayCurrency AND i.SourcePostingDate<=@PayDate
                JOIN Purchasing.SupplierBillPostings b ON b.TenantId=i.TenantId AND b.BillId=i.BillId AND b.RevisionId=i.SourceRevisionId
                WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.itemId'))=a.ItemId AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.billId'))=a.BillId
                  AND TRY_CONVERT(decimal(28,4),JSON_VALUE(original.value,'$.amount'))=a.Amount))
              THROW 51004,'Complete immediate payment allocations are unavailable.',1;
            DECLARE @PayAllocated decimal(38,4)=COALESCE((SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @PayApplications),0);
            IF @PayAllocated>@PayAmount THROW 51009,'Payment allocations exceed gross funding.',1;
            DECLARE @PayNet decimal(28,4)=@PayAmount-@PayAllocated;
            DECLARE @PayExpected TABLE(AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
            INSERT @PayExpected SELECT AccountId,AccountVersion,SUM(Amount),0 FROM @PayApplications GROUP BY AccountId,AccountVersion;
            IF @PayNet>0 INSERT @PayExpected VALUES(@PayAdvance,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.version')),@PayNet,0);
            INSERT @PayExpected VALUES(@PayFunding,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')),0,@PayAmount);
            IF EXISTS(SELECT AccountId,AccountVersion,Debit,Credit FROM @PayExpected EXCEPT
                SELECT AccountId,AccountVersion,Debit,Credit FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@PayJournal)
              OR (SELECT COUNT(*) FROM @PayExpected)<>(SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@PayJournal)
              OR EXISTS(SELECT 1 FROM @PayApplications a WHERE NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=@PayJournal AND l.Ordinal=a.Ordinal AND l.AccountId=a.AccountId AND l.AccountVersion=a.AccountVersion AND l.AccountPurpose='SupplierPayable'))
              OR EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions WHERE TenantId=@TenantId AND JournalId=@PayJournal)
              THROW 51004,'Supplier payment journal controls are not conserved.',1;
            DECLARE @PayEffects TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,EventKind varchar(24),Amount decimal(28,4),Ordinal int);
            INSERT @PayEffects VALUES(NEWID(),@Pay,'Open',@PayAmount,NULL);
            INSERT @PayEffects SELECT NEWID(),a.ItemId,'Apply',-a.Amount,a.Ordinal FROM @PayApplications a
              UNION ALL SELECT NEWID(),@Pay,'Apply',-Amount,NULL FROM @PayApplications;
            DECLARE @PayMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,@PayDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @PayEffects FOR JSON PATH);
            EXEC Purchasing.AssertSupplierAvailability @TenantId,@PayMovements;
            INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@TenantId,@GroupId,'RecordPayment',@Pay,@RecordedAtUtc);
            INSERT Purchasing.SupplierPayments(TenantId,Id,RevisionId,SupplierId,PurchaseOrderId,Currency,PaymentDate,EffectiveDate,PostingDate,Amount,Method,
              FundingAccountId,FundingAccountVersion,FundingAccountPurpose,Reference,Notes,EvidenceJson,ActorId,GroupId,RecordedAtUtc)
              VALUES(@TenantId,@Pay,@PayRevision,@PaySupplier,@PayPo,@PayCurrency,@PayPaymentDate,@PayEffective,@PayDate,@PayAmount,
                JSON_VALUE(@PaySnapshot,'$.command.method'),@PayFunding,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')),
                JSON_VALUE(@PaySnapshot,'$.fundingAccount.purpose'),JSON_VALUE(@PaySnapshot,'$.command.reference'),JSON_VALUE(@PaySnapshot,'$.command.notes'),@PaySnapshot,@PayActor,@GroupId,@RecordedAtUtc);
            INSERT Purchasing.SupplierPaymentVersions(TenantId,PaymentId) VALUES(@TenantId,@Pay);
            INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,SourceId,SourceRevisionId,SourcePostingDate,SourceSnapshotJson,RecordedAtUtc)
              VALUES(@TenantId,@Pay,'Advance',@PaySupplier,@PayPo,@PayCurrency,'SupplierPayment',@Pay,@PayRevision,@PayDate,@PaySnapshot,@RecordedAtUtc);
            INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@TenantId,@Pay);
            INSERT Purchasing.SupplierApplications(TenantId,Id,GroupId,FundingItemId,DebtItemId,PostingDate,Amount,ActorId,RecordedAtUtc)
              SELECT @TenantId,Id,@GroupId,@Pay,ItemId,@PayDate,Amount,@PayActor,@RecordedAtUtc FROM @PayApplications;
            INSERT Purchasing.SupplierApplicationVersions(TenantId,ApplicationId) SELECT @TenantId,Id FROM @PayApplications;
            UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v JOIN @PayApplications a ON a.ItemId=v.ItemId WHERE v.TenantId=@TenantId;
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              SELECT @TenantId,Id,ItemId,@GroupId,EventKind,@PaySource,@PayDate,Amount,@RecordedAtUtc FROM @PayEffects;
            -- Payment groups attribute NET control effects; the gross opening and
            -- immediate funding reductions are proved together, including zero-net.
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @TenantId,NEWID(),@GroupId,e.Id,@PayJournal,l.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,
                CASE WHEN e.EventKind='Open' THEN @PayNet ELSE e.Amount END
              FROM @PayEffects e JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=@PayJournal
                AND ((e.Ordinal=l.Ordinal AND e.ItemId<>@Pay) OR (e.EventKind='Open' AND l.AccountId=@PayAdvance AND @PayNet>0));
            RETURN;
          END;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.RecordSupplierPayment
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
            IF @TenantId IS NULL THROW 51003,'Current supplier payment authority is required.',1;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierPaymentsRecord'; END TRY
            BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier payment authority is required.',1; THROW; END CATCH;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
            EXEC Purchasing.ValidateSupplierFinancialCommand N'RecordSupplierPayment',@Command,@Canonical OUTPUT;
            IF (SELECT COUNT(*) FROM OPENJSON(@Canonical))<>20
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
                ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion','supplierId','currency',
                 'postingDate','paymentId','paymentRevisionId','paymentDate','effectiveDate','amount','method','fundingAccountId',
                 'expectedFundingAccountVersion','reference','notes','evidence','allocations')
                OR ([key]='schemaVersion' AND type<>2) OR ([key]='allocations' AND type<>4) OR ([key]='evidence' AND type<>5)
                OR ([key] IN('reference','notes') AND type NOT IN(0,1))
                OR ([key] NOT IN('schemaVersion','allocations','evidence','reference','notes') AND type<>1))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations') e WHERE e.type<>5
                OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>4
                OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('billId','itemId','expectedItemVersion','amount') OR p.type<>1))
              THROW 51000,'Invalid supplier payment fields.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','paymentId','paymentRevisionId','fundingAccountId','expectedFundingAccountVersion')
                AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
              OR DATALENGTH(JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'))<>36
              OR TRY_CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1) IS NULL
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations') e CROSS APPLY OPENJSON(e.value) p WHERE
                (p.[key]='billId' AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
                OR (p.[key]='expectedItemVersion' AND (DATALENGTH(p.value)<>36 OR TRY_CONVERT(binary(8),p.value,1) IS NULL))
                OR (p.[key]='amount' AND TRY_CONVERT(decimal(28,4),p.value)<=0))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('postingDate','paymentDate','effectiveDate')
                AND (DATALENGTH(value)<>20 OR TRY_CONVERT(date,value,23) IS NULL))
              OR DATALENGTH(JSON_VALUE(@Canonical,'$.currency'))<>6
              OR TRY_CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.amount'))<=0
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key]='method' AND value COLLATE Latin1_General_100_BIN2 NOT IN('Bank','Cash'))
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE ([key]='reference' AND DATALENGTH(value)>400) OR ([key]='notes' AND DATALENGTH(value)>4000))
              THROW 51000,'Invalid supplier payment values.',1;
            DECLARE @EvidenceInput nvarchar(max)=JSON_QUERY(@Canonical,'$.evidence');
            IF (SELECT COUNT(*) FROM OPENJSON(@EvidenceInput))<>2
              OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('documents','missingEvidenceReason')
                OR ([key]='documents' AND type<>4) OR ([key]='missingEvidenceReason' AND type NOT IN(0,1)))
              OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key]='missingEvidenceReason' AND DATALENGTH(value)>4000)
              OR (NOT EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents')) AND NULLIF(TRIM(JSON_VALUE(@EvidenceInput,'$.missingEvidenceReason')),'') IS NULL)
              OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents') d WHERE d.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(d.value))<>2
                OR EXISTS(SELECT 1 FROM OPENJSON(d.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('documentId','revisionId')
                  OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
              THROW 51000,'Invalid supplier payment evidence.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations'))
            BEGIN
              BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
              BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier allocation authority is required.',1; THROW; END CATCH;
            END;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                AND Operation='RecordSupplierPayment' AND ActorId=@ActorId AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
                THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            DECLARE @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
              @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
              @Payment uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')),
              @Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentRevisionId')),
              @Funding uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.fundingAccountId')),
              @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
              @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@PaymentDate date=CONVERT(date,JSON_VALUE(@Canonical,'$.paymentDate'),23),
              @Effective date=CONVERT(date,JSON_VALUE(@Canonical,'$.effectiveDate'),23),@Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),
              @Amount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.amount')),@Group uniqueidentifier=NEWID(),
              @Now datetimeoffset=SYSUTCDATETIME(),@Payload nvarchar(max),@Scale int,@Advance uniqueidentifier;
            IF @PaymentDate>@Date OR @Effective>@Date OR @PaymentDate>CONVERT(date,@Now)
              THROW 51000,'Supplier payment dates are invalid.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierPayments WHERE TenantId=@TenantId AND (Id=@Payment OR RevisionId=@Revision))
              THROW 51009,'Supplier payment source already exists.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po AND State='Ordered' AND IsDeleted=0
                AND SupplierId=@Supplier AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
              THROW 51009,'Purchase revision changed.',1;
            SELECT @Payload=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
            IF @Payload IS NULL THROW 51009,'Accounting configuration changed.',1;
            SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Payload,'$.policies.scale'));
            SELECT @Advance=TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')) FROM OPENJSON(@Payload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='SupplierAdvance';
            IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR @Currency<>JSON_VALUE(@Payload,'$.policies.currency')
              OR @Amount<>ROUND(@Amount,@Scale,1) THROW 51000,'Payment currency or precision is invalid.',1;
            IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Funding AND ArchivedAtUtc IS NULL
                AND Type='Asset' AND Purpose COLLATE Latin1_General_100_BIN2 IN('Bank','Cash'))
              THROW 51004,'Payment funding account must be active Bank or Cash.',1;
            IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding
                AND Version=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedFundingAccountVersion')))
              THROW 51009,'Funding account revision changed.',1;
            IF (SELECT COUNT(*) FROM OPENJSON(@Payload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='SupplierAdvance')<>1
              OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Advance AND ArchivedAtUtc IS NULL AND Type='Asset' AND Purpose='SupplierAdvance')
              THROW 51004,'Current supplier advance account is unavailable.',1;
            DECLARE @Targets TABLE(Position int PRIMARY KEY,ApplicationId uniqueidentifier,ItemId uniqueidentifier,BillId uniqueidentifier,Amount decimal(28,4),
              AccountId uniqueidentifier,AccountVersion uniqueidentifier,Ordinal int);
            INSERT @Targets(Position,ApplicationId,ItemId,BillId,Amount)
              SELECT CONVERT(int,[key]),NEWID(),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.billId')),
                CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Canonical,'$.allocations');
            IF EXISTS(SELECT 1 FROM @Targets WHERE Amount<>ROUND(Amount,@Scale,1)) THROW 51000,'Allocation precision is invalid.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations') t WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK)
              WHERE v.TenantId=@TenantId AND v.ItemId=CONVERT(uniqueidentifier,JSON_VALUE(t.value,'$.itemId')) AND v.RowVersion=CONVERT(binary(8),JSON_VALUE(t.value,'$.expectedItemVersion'),1)))
              THROW 51009,'Supplier item revision changed.',1;
            IF EXISTS(SELECT 1 FROM @Targets t WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems i
              JOIN Purchasing.SupplierBillPostings b ON b.TenantId=i.TenantId AND b.BillId=i.BillId AND b.RevisionId=i.SourceRevisionId
              WHERE i.TenantId=@TenantId AND i.Id=t.ItemId AND i.BillId=t.BillId AND i.SourceKind='SupplierBill' AND i.SourceId=i.BillId
                AND i.Kind='Payable' AND i.SupplierId=@Supplier AND i.PurchaseOrderId=@Po AND i.Currency=@Currency AND i.SourcePostingDate<=@Date))
              THROW 51004,'Posted supplier bill is unavailable on the payment date.',1;
            IF EXISTS(SELECT 1 FROM @Targets t WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemControl(@TenantId,t.ItemId))<>1)
              THROW 51004,'Historical supplier control evidence is unavailable.',1;
            UPDATE t SET AccountId=c.AccountId,AccountVersion=c.AccountVersion FROM @Targets t CROSS APPLY Purchasing.SupplierItemControl(@TenantId,t.ItemId) c;
            -- Different bills can own different immutable versions of one account.
            -- Preserve each historical snapshot in its own net debit line.
            DECLARE @Allocated decimal(38,4)=COALESCE((SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets),0);
            IF @Allocated>@Amount THROW 51009,'Payment allocations exceed gross funding.',1;
            DECLARE @Net decimal(28,4)=@Amount-@Allocated;
            DECLARE @Line TABLE(Ordinal int,AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
            INSERT @Line SELECT ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion),AccountId,AccountVersion,SUM(Amount),0 FROM @Targets GROUP BY AccountId,AccountVersion;
            UPDATE t SET Ordinal=l.Ordinal FROM @Targets t JOIN @Line l ON l.AccountId=t.AccountId AND l.AccountVersion=t.AccountVersion;
            IF @Net>0 INSERT @Line SELECT (SELECT COUNT(*)+1 FROM @Line),Id,Version,@Net,0 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Advance;
            INSERT @Line SELECT (SELECT COUNT(*)+1 FROM @Line),Id,Version,0,@Amount FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding;
            DECLARE @Documents nvarchar(max);
            EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@EvidenceInput,@Documents OUTPUT;
            DECLARE @Snapshot nvarchar(max)=(SELECT 1 schemaVersion,@Group groupId,JSON_QUERY(@Canonical) command,
              JSON_QUERY((SELECT Id id,Version version,Code code,Name name,Type type,Purpose purpose FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Advance FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) advanceAccount,
              JSON_QUERY((SELECT Id id,Version version,Code code,Name name,Type type,Purpose purpose FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) fundingAccount,
              JSON_QUERY((SELECT Id id,Name name FROM Purchasing.Suppliers WHERE TenantId=@TenantId AND Id=@Supplier FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) supplier,
              JSON_QUERY((SELECT Id id,Revision revision,OrderDate orderDate,ContentJson content FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@Po FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) purchaseOrder,
              JSON_QUERY((SELECT JSON_QUERY(@Documents) documents,JSON_VALUE(@EvidenceInput,'$.missingEvidenceReason') missingEvidenceReason FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) evidence,
              JSON_QUERY((SELECT ApplicationId applicationId,ItemId itemId,BillId billId,CONVERT(nvarchar(60),Amount) amount,Ordinal ordinal FROM @Targets ORDER BY Position FOR JSON PATH)) allocations
              FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            DECLARE @Lines nvarchar(max)=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit FROM @Line ORDER BY Ordinal FOR JSON PATH),
              @Controls nvarchar(max)=(SELECT Ordinal ordinal,ItemId itemId FROM @Targets ORDER BY Position FOR JSON PATH),@KernelRequest uniqueidentifier=NEWID();
            IF NOT EXISTS(SELECT 1 FROM @Targets) SET @Controls=NULL;
            DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@KernelRequest,N'SupplierPaymentsRecord',N'Supplier.Payment',1,@Canonical,
              N'SupplierPayment',@Payment,@Revision,N'Payment',1,@Config,@Currency,@PaymentDate,@Effective,@Date,NULL,NULL,@Snapshot,@Lines,@Controls;
            UPDATE s SET RecordedAtUtc=@Now FROM Accounting.SourceEvents s JOIN @Posted p ON p.SourceEventId=s.Id WHERE s.TenantId=@TenantId;
            UPDATE j SET RecordedAtUtc=@Now FROM Accounting.JournalEntries j JOIN @Posted p ON p.JournalId=j.Id WHERE j.TenantId=@TenantId;
            UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND RequestId=@KernelRequest;
            UPDATE a SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents a JOIN @Posted p ON p.JournalId=a.TargetId WHERE a.TenantId=@TenantId AND a.Action='Accounting.PostJournal';
            DECLARE @Events nvarchar(max)=(SELECT @Payment paymentId,JournalId journalId FROM @Posted FOR JSON PATH);
            EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@Events;
            SET @Result=(SELECT @Payment paymentId,@Revision paymentRevisionId,@Payment advanceItemId,@Group groupId,@Now recordedAtUtc,
              JSON_QUERY(COALESCE((SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),ApplicationId)+N'"'),N',') WITHIN GROUP(ORDER BY Position)+N']' FROM @Targets),N'[]')) applicationIds,
              JSON_QUERY((SELECT N'["'+CONVERT(nvarchar(36),JournalId)+N'"]' FROM @Posted)) journalIds,
              (SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierPaymentVersions WHERE TenantId=@TenantId AND PaymentId=@Payment) version FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,Operation,ActorId,CanonicalInput,InputSha256,GroupId,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@RequestId,'RecordSupplierPayment',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Group,@Result,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
