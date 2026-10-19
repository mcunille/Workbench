-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierCreditCapacity
  @TenantId uniqueidentifier,@Sources nvarchar(max),@Positions nvarchar(max) OUTPUT,@Adjustments nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Credit proposal requires its owner transaction.',1;
  DECLARE @Source TABLE(Id uniqueidentifier PRIMARY KEY,PostingDate date,Replacement nvarchar(max));
  INSERT @Source SELECT Id,PostingDate,Replacement FROM OPENJSON(@Sources)
    WITH(Id uniqueidentifier '$.sourceId',PostingDate date '$.postingDate',Replacement nvarchar(max) '$.replacement' AS JSON);
  DECLARE @New TABLE(SourceId uniqueidentifier,EventId uniqueidentifier,ComponentKey nvarchar(200),Amount decimal(28,4),PostingDate date,Treatment varchar(24));
  INSERT @New SELECT source.Id,CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')),JSON_VALUE(claim.value,'$.componentKey'),
    CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount')),CONVERT(date,JSON_VALUE(source.Replacement,'$.postingDate'),23),position.Treatment
    FROM @Source source CROSS APPLY OPENJSON(source.Replacement,'$.claims') claim
    CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')),
      JSON_VALUE(claim.value,'$.componentKey'),CONVERT(date,JSON_VALUE(source.Replacement,'$.postingDate'),23)) position;
  IF (SELECT COUNT(*) FROM @New)<>(SELECT COUNT(*) FROM @Source source CROSS APPLY OPENJSON(source.Replacement,'$.claims'))
    THROW 51004,'Replacement requires a supported authentic cost position.',1;
  IF EXISTS(SELECT 1 FROM @Source source JOIN Accounting.Configurations config ON config.TenantId=@TenantId
      AND config.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.expectedConfigurationVersion'))
    CROSS APPLY OPENJSON(source.Replacement,'$.claims') claim
    WHERE CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount')),
      TRY_CONVERT(int,JSON_VALUE(config.Payload,'$.policies.scale')),1))
    THROW 51000,'Replacement claims exceed configured currency precision.',1;
  DECLARE @Affected TABLE(EventId uniqueidentifier,ComponentKey nvarchar(200),PRIMARY KEY(EventId,ComponentKey));
  INSERT @Affected SELECT claim.RecognitionEventId,claim.ComponentKey FROM Purchasing.SupplierCreditClaims claim
    JOIN @Source source ON source.Id=claim.CreditId WHERE claim.TenantId=@TenantId
    UNION SELECT EventId,ComponentKey FROM @New;
  DECLARE @Stream TABLE(EventId uniqueidentifier,ComponentKey nvarchar(200),PostingDate date,Amount decimal(28,4),Treatment varchar(24));
  INSERT @Stream SELECT claim.RecognitionEventId,claim.ComponentKey,credit.PostingDate,claim.Amount,claim.Treatment
    FROM Purchasing.SupplierCreditClaims claim JOIN Purchasing.SupplierCredits credit ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
    WHERE claim.TenantId=@TenantId AND EXISTS(SELECT 1 FROM @Affected affected WHERE affected.EventId=claim.RecognitionEventId);
  INSERT @Stream SELECT claim.RecognitionEventId,claim.ComponentKey,correction.PostingDate,-claim.Amount,claim.Treatment
    FROM Purchasing.SupplierCreditClaims claim JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=claim.TenantId
      AND correction.SourceKind='SupplierCredit' AND correction.SourceId=claim.CreditId
    WHERE claim.TenantId=@TenantId AND EXISTS(SELECT 1 FROM @Affected affected WHERE affected.EventId=claim.RecognitionEventId);
  INSERT @Stream SELECT claim.RecognitionEventId,claim.ComponentKey,source.PostingDate,-claim.Amount,claim.Treatment
    FROM Purchasing.SupplierCreditClaims claim JOIN @Source source ON source.Id=claim.CreditId WHERE claim.TenantId=@TenantId;
  INSERT @Stream SELECT EventId,ComponentKey,PostingDate,Amount,Treatment FROM @New;
  IF (SELECT COUNT(*) FROM @Stream)>1000 THROW 51000,'The complete component capacity stream exceeds its event bound.',1;
  DECLARE @Balances TABLE(EventId uniqueidentifier,ComponentKey nvarchar(200),PostingDate date,Claimed decimal(38,4),Capacity decimal(28,4));
  ;WITH dates AS(SELECT EventId,ComponentKey,PostingDate,SUM(CONVERT(decimal(38,4),Amount)) Amount FROM @Stream GROUP BY EventId,ComponentKey,PostingDate),
  balances AS(SELECT EventId,ComponentKey,PostingDate,SUM(Amount) OVER(PARTITION BY EventId,ComponentKey ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) Claimed FROM dates)
  INSERT @Balances SELECT balances.EventId,balances.ComponentKey,balances.PostingDate,balances.Claimed,
    CONVERT(decimal(28,4),component.Amount+COALESCE(CONVERT(decimal(28,4),assigned.Amount),0))
    FROM balances JOIN Purchasing.RecognitionComponents component ON component.TenantId=@TenantId AND component.EventId=balances.EventId AND component.ComponentKey=balances.ComponentKey
    OUTER APPLY(SELECT SUM(CASE WHEN adjustment.Kind='Discount' THEN -CONVERT(decimal(38,4),adjustment.Amount)
        WHEN adjustment.Kind='Rounding' THEN CONVERT(decimal(38,4),adjustment.Amount) ELSE 0 END) Amount
      FROM Purchasing.RecognitionComponents adjustment WHERE adjustment.TenantId=component.TenantId AND adjustment.EventId=component.EventId
        AND adjustment.AssignedCostComponentKey=component.ComponentKey AND adjustment.Kind IN('Discount','Rounding')) assigned;
  IF EXISTS(SELECT 1 FROM @Balances WHERE Claimed<0 OR Claimed>Capacity OR TRY_CONVERT(decimal(28,4),Claimed) IS NULL)
    THROW 51009,'The complete proposal exceeds an original component posting boundary.',1;
  ;WITH dates AS(SELECT EventId,PostingDate,SUM(CONVERT(decimal(38,4),Amount)) Amount FROM @Stream GROUP BY EventId,PostingDate),
  balances AS(SELECT EventId,PostingDate,SUM(Amount) OVER(PARTITION BY EventId ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) Claimed FROM dates)
  SELECT * INTO #WholeCreditCapacity FROM balances;
  IF EXISTS(SELECT 1 FROM #WholeCreditCapacity balance WHERE balance.Claimed<0 OR balance.Claimed>(
    SELECT SUM(CASE WHEN component.Kind='Discount' THEN -CONVERT(decimal(38,4),component.Amount)
      WHEN component.Kind='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),component.Amount) END)
    FROM Purchasing.RecognitionComponents component WHERE component.TenantId=@TenantId AND component.EventId=balance.EventId))
    THROW 51009,'The complete proposal exceeds original whole-unit cost.',1;
  DECLARE @Adjustment TABLE(SourceId uniqueidentifier,EventId uniqueidentifier,RecognitionId uniqueidentifier,PostingDate date,Amount decimal(28,4));
  INSERT @Adjustment
    SELECT source.Id,claim.RecognitionEventId,recognition.Id,
      CASE WHEN recognition.PostingDate>source.PostingDate THEN recognition.PostingDate ELSE source.PostingDate END,SUM(claim.Amount)
    FROM @Source source JOIN Purchasing.SupplierCreditClaims claim ON claim.TenantId=@TenantId AND claim.CreditId=source.Id AND claim.Treatment='Prepayment'
    JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=claim.TenantId AND matched.InvoiceEventId=claim.RecognitionEventId
    JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=matched.TenantId AND recognition.Id=matched.RecognitionEventId
    JOIN Purchasing.SupplierCredits credit ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
    WHERE credit.PostingDate<=recognition.PostingDate
    GROUP BY source.Id,claim.RecognitionEventId,recognition.Id,recognition.PostingDate,source.PostingDate;
  INSERT @Adjustment
    SELECT proposed.SourceId,proposed.EventId,recognition.Id,recognition.PostingDate,-SUM(proposed.Amount)
    FROM @New proposed JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=@TenantId AND matched.InvoiceEventId=proposed.EventId
    JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=matched.TenantId AND recognition.Id=matched.RecognitionEventId
    WHERE proposed.Treatment='Prepayment' AND proposed.PostingDate<recognition.PostingDate
    GROUP BY proposed.SourceId,proposed.EventId,recognition.Id,recognition.PostingDate;
  IF EXISTS(SELECT 1 FROM @Adjustment adjustment JOIN Purchasing.RecognitionSideEvents recognition ON recognition.TenantId=@TenantId AND recognition.Id=adjustment.RecognitionId
    JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=recognition.TenantId AND unit.Id=recognition.UnitId WHERE unit.Classification<>'Expense')
    THROW 51004,'A remaining inventory position owner is required for this recognition dependency.',1;
  SET @Adjustments=(SELECT adjustment.SourceId sourceId,adjustment.EventId invoiceEventId,adjustment.RecognitionId recognitionEventId,
    adjustment.PostingDate postingDate,CONVERT(nvarchar(60),SUM(adjustment.Amount)) amount,
    expense.AccountId expenseAccountId,expense.AccountVersion expenseAccountVersion,expense.JournalId expenseJournalId,expense.Ordinal expenseOrdinal,
    prepayment.AccountId prepaymentAccountId,prepayment.AccountVersion prepaymentAccountVersion,prepayment.JournalId prepaymentJournalId,prepayment.Ordinal prepaymentOrdinal,
    CONVERT(varchar(64),recognition.EvidenceSha256,2) recognitionEvidenceSha256
    FROM @Adjustment adjustment
    JOIN Purchasing.RecognitionSideEvents recognition ON recognition.TenantId=@TenantId AND recognition.Id=adjustment.RecognitionId
    JOIN Purchasing.RecognitionSideEvents invoice ON invoice.TenantId=@TenantId AND invoice.Id=adjustment.EventId
    CROSS APPLY Purchasing.SupplierRecognitionExpenseControl(@TenantId,recognition.Id) expense
    JOIN Accounting.JournalLines prepayment ON prepayment.TenantId=invoice.TenantId AND prepayment.JournalId=invoice.JournalId
      AND prepayment.AccountId=TRY_CONVERT(uniqueidentifier,(SELECT JSON_VALUE(value,'$.accountId') FROM OPENJSON(invoice.EvidenceJson,'$.accountMappings') WHERE JSON_VALUE(value,'$.slot')='Prepayment'))
      AND prepayment.Debit>0
    GROUP BY adjustment.SourceId,adjustment.EventId,adjustment.RecognitionId,adjustment.PostingDate,expense.AccountId,expense.AccountVersion,expense.JournalId,expense.Ordinal,
      prepayment.AccountId,prepayment.AccountVersion,prepayment.JournalId,prepayment.Ordinal,recognition.EvidenceSha256
    HAVING SUM(adjustment.Amount)<>0 ORDER BY adjustment.SourceId,adjustment.EventId,adjustment.PostingDate FOR JSON PATH);
  IF (SELECT COUNT(*) FROM OPENJSON(@Adjustments))<>(SELECT COUNT(*) FROM(
    SELECT SourceId,EventId,RecognitionId,PostingDate FROM @Adjustment GROUP BY SourceId,EventId,RecognitionId,PostingDate HAVING SUM(Amount)<>0) expected)
    THROW 51004,'A recognition dependency has no authentic original cost/prepayment snapshots.',1;
  SET @Positions=(SELECT EventId recognitionEventId,ComponentKey componentKey,PostingDate postingDate,
    CONVERT(nvarchar(60),Claimed) claimed,CONVERT(nvarchar(60),Capacity) capacity,
    CONVERT(nvarchar(60),Capacity-CONVERT(decimal(28,4),Claimed)) remaining
    FROM @Balances ORDER BY EventId,ComponentKey,PostingDate FOR JSON PATH);
END;
