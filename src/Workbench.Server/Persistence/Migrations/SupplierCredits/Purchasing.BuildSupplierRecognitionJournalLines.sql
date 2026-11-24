-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierRecognitionJournalLines
  @TenantId uniqueidentifier,@Policy nvarchar(max),@Side varchar(16),@Classification varchar(16),@Cost decimal(28,4),@Tax decimal(28,4),
  @HasPrior bit,@PriorCost decimal(28,4),@PriorMappings nvarchar(max),@Mappings nvarchar(max) OUTPUT,@LinesJson nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Lines TABLE(Slot varchar(40),AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4),Historical bit);
  DECLARE @Variance decimal(28,4)=@Cost-@PriorCost;
  IF @HasPrior=0
    INSERT @Lines(Slot,Debit,Credit,Historical) VALUES(CASE WHEN @Side='Recognition' THEN @Classification ELSE 'Prepayment' END,@Cost,0,0),
      (CASE WHEN @Side='Recognition' THEN 'GoodsReceivedNotInvoiced' ELSE 'SupplierPayable' END,0,@Cost+@Tax,0);
  ELSE IF @Side='Invoice'
    INSERT @Lines(Slot,Debit,Credit,Historical) VALUES('GoodsReceivedNotInvoiced',@PriorCost,0,1),
      (@Classification,CASE WHEN @Variance>0 THEN @Variance ELSE 0 END,CASE WHEN @Variance<0 THEN -@Variance ELSE 0 END,CASE WHEN @Classification='Expense' THEN 1 ELSE 0 END),
      ('SupplierPayable',0,@Cost+@Tax,0);
  ELSE
    INSERT @Lines(Slot,Debit,Credit,Historical) VALUES(@Classification,@PriorCost,0,0),('Prepayment',0,@PriorCost,1);
  IF @Tax>0 INSERT @Lines(Slot,Debit,Credit,Historical) VALUES('RecoverableTax',@Tax,0,0);
  UPDATE l SET AccountId=a.Id,AccountVersion=a.Version FROM @Lines l
    JOIN OPENJSON(@Policy,'$.mappings') m ON JSON_VALUE(m.value,'$.slot') COLLATE Latin1_General_100_BIN2=l.Slot
    JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=@TenantId AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
      AND a.ArchivedAtUtc IS NULL AND a.Type=CASE WHEN l.Slot='Expense' THEN 'Expense' WHEN l.Slot IN ('GoodsReceivedNotInvoiced','SupplierPayable') THEN 'Liability' ELSE 'Asset' END
      AND a.Purpose=CASE WHEN l.Slot='SupplierPayable' THEN 'SupplierPayable' ELSE 'General' END WHERE l.Historical=0;
  UPDATE l SET AccountId=a.Id,AccountVersion=a.Version FROM @Lines l
    JOIN OPENJSON(@PriorMappings) m ON JSON_VALUE(m.value,'$.slot') COLLATE Latin1_General_100_BIN2=l.Slot
    JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=@TenantId AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
      AND a.ArchivedAtUtc IS NULL AND a.Type=CASE WHEN l.Slot='Expense' THEN 'Expense' WHEN l.Slot='GoodsReceivedNotInvoiced' THEN 'Liability' ELSE 'Asset' END
      AND a.Purpose='General' WHERE l.Historical=1;
  IF EXISTS(SELECT 1 FROM @Lines WHERE AccountId IS NULL) OR EXISTS(SELECT AccountId FROM @Lines GROUP BY AccountId HAVING COUNT(*)>1)
    THROW 51004,'A required recognition mapping is unavailable or incompatible.',1;
  SELECT @Mappings=(SELECT Slot slot,AccountId accountId,AccountVersion accountVersion FROM @Lines ORDER BY Slot FOR JSON PATH),
    @LinesJson=(SELECT ROW_NUMBER() OVER(ORDER BY Slot) ordinal,AccountId accountId,AccountVersion accountVersion,
      CONVERT(varchar(30),Debit) debit,CONVERT(varchar(30),Credit) credit FROM @Lines WHERE Debit<>0 OR Credit<>0 ORDER BY Slot FOR JSON PATH);
END;
