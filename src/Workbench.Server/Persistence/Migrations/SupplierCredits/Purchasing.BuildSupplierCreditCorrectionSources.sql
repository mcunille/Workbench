-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierCreditCorrectionSources
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Sources nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Root uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23);
  DECLARE @Requested TABLE(Id uniqueidentifier PRIMARY KEY,Version binary(8),Replacement nvarchar(max));
  INSERT @Requested VALUES(@Root,CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedSourceVersion'),1),JSON_QUERY(@Canonical,'$.replacement'));
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')<>'SupplierCredit'
    OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId'))=@Root)
    THROW 51004,'Only affected credit source decisions belong to a credit correction.',1;
  INSERT @Requested SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),
    CONVERT(binary(8),JSON_VALUE(value,'$.expectedSourceVersion'),1),JSON_QUERY(value,'$.replacement') FROM OPENJSON(@Canonical,'$.sourceDecisions');
  DECLARE @Components TABLE(CreditId uniqueidentifier,EventId uniqueidentifier,ComponentKey nvarchar(4000));
  INSERT @Components
    SELECT claim.CreditId,claim.RecognitionEventId,claim.ComponentKey FROM Purchasing.SupplierCreditClaims claim
      JOIN @Requested requested ON requested.Id=claim.CreditId WHERE claim.TenantId=@TenantId
    UNION SELECT requested.Id,TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')),JSON_VALUE(claim.value,'$.componentKey')
      FROM @Requested requested CROSS APPLY OPENJSON(requested.Replacement,'$.claims') claim
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')) IS NOT NULL AND JSON_VALUE(claim.value,'$.componentKey') IS NOT NULL;
  IF (SELECT COUNT(*) FROM @Components)>1000 THROW 51000,'The complete component dependency graph exceeds its bound.',1;
  DECLARE @Connected TABLE(Id uniqueidentifier PRIMARY KEY);
  INSERT @Connected VALUES(@Root);
  DECLARE @ConnectedCount int=1;
  WHILE @ConnectedCount>0
  BEGIN
    INSERT @Connected SELECT DISTINCT candidate.CreditId FROM @Components candidate
      JOIN @Components affected ON affected.EventId=candidate.EventId AND affected.ComponentKey=candidate.ComponentKey
      JOIN @Connected connected ON connected.Id=affected.CreditId
      WHERE NOT EXISTS(SELECT 1 FROM @Connected present WHERE present.Id=candidate.CreditId);
    SET @ConnectedCount=@@ROWCOUNT;
  END;
  IF (SELECT COUNT(*) FROM @Connected)<>(SELECT COUNT(*) FROM @Requested)
    THROW 51004,'A declared credit does not share an affected original or successor component.',1;
  DECLARE @Resolved TABLE(Id uniqueidentifier PRIMARY KEY,RevisionId uniqueidentifier,Version binary(8),Amount decimal(28,4),
    PostingDate date,CorrectionDate date,JournalId uniqueidentifier,SourceEventId uniqueidentifier,SnapshotSha256 binary(32),
    Replacement nvarchar(max),RequestedAllocations nvarchar(max),ReplacementId uniqueidentifier,ReplacementRevisionId uniqueidentifier);
  INSERT @Resolved(Id,RevisionId,Version,Amount,PostingDate,CorrectionDate,JournalId,SourceEventId,SnapshotSha256,Replacement)
    SELECT credit.Id,credit.RevisionId,version.RowVersion,credit.Total,credit.PostingDate,
      CASE WHEN credit.PostingDate>@Date THEN credit.PostingDate ELSE @Date END,journal.Id,sourceEvent.Id,sourceEvent.SnapshotSha256,requested.Replacement
    FROM @Requested requested JOIN Purchasing.SupplierCredits credit WITH(UPDLOCK,HOLDLOCK) ON credit.TenantId=@TenantId AND credit.Id=requested.Id
      AND credit.SupplierId=@Supplier AND credit.PurchaseOrderId=@Po AND credit.Currency=@Currency
    JOIN Purchasing.SupplierCreditVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=credit.TenantId AND version.CreditId=credit.Id
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=credit.TenantId AND sourceEvent.SourceKind='SupplierCredit'
      AND sourceEvent.SourceId=credit.Id AND sourceEvent.SourceRevision=credit.RevisionId AND sourceEvent.EventKind='Credit'
    JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
    CROSS APPLY Purchasing.SupplierCreditControl(@TenantId,credit.Id) control;
  IF (SELECT COUNT(*) FROM @Resolved)<>(SELECT COUNT(*) FROM @Requested)
    THROW 51004,'Authentic credit source ownership is unavailable.',1;
  IF EXISTS(SELECT 1 FROM @Requested requested JOIN @Resolved resolved ON resolved.Id=requested.Id WHERE requested.Version<>resolved.Version)
    OR EXISTS(SELECT 1 FROM @Resolved resolved JOIN Purchasing.SupplierSourceCorrections correction
      ON correction.TenantId=@TenantId AND correction.SourceKind='SupplierCredit' AND correction.SourceId=resolved.Id)
    THROW 51009,'A credit source version changed or already has a successor.',1;
  IF EXISTS(SELECT 1 FROM @Resolved WHERE Id=@Root AND PostingDate>@Date)
    THROW 51004,'Correction date precedes its source.',1;
  DECLARE @Id uniqueidentifier,@Replacement nvarchar(max),@ReplacementCanonical nvarchar(max),@Derived nvarchar(max),@Claims nvarchar(max),@ChildDate date;
  DECLARE replacements CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Replacement,CorrectionDate FROM @Resolved WHERE Replacement IS NOT NULL ORDER BY Id;
  OPEN replacements; FETCH NEXT FROM replacements INTO @Id,@Replacement,@ChildDate;
  WHILE @@FETCH_STATUS=0
  BEGIN
    EXEC Purchasing.CanonicalizeSupplierCredit N'PostSupplierCredit',@Replacement,@ReplacementCanonical OUTPUT;
    IF TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.purchaseOrderId'))<>@Po
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.supplierId'))<>@Supplier
      OR JSON_VALUE(@ReplacementCanonical,'$.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedConfigurationVersion')<>JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedPurchaseOrderVersion')<>JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion')
      OR CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.postingDate'),23)<@ChildDate
      OR CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.documentDate'),23)>CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.postingDate'),23)
      OR CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.effectiveDate'),23)>CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.postingDate'),23)
      THROW 51000,'Replacement must preserve source scope and eligible dates.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND
      (Id=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditId'))
        OR RevisionId=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditRevisionId'))))
      THROW 51009,'A replacement credit requires fresh source identities.',1;
    DECLARE @Reference nvarchar(200)=Purchasing.NormalizeBillReference(JSON_VALUE(@ReplacementCanonical,'$.reference')),
      @Documents nvarchar(max),@Evidence nvarchar(max)=JSON_QUERY(@ReplacementCanonical,'$.evidence');
    IF COALESCE(LEN(@Reference),0)=0 THROW 51000,'A successor credit reference is required.',1;
    IF EXISTS(SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND SupplierId=@Supplier AND NormalizedReference=@Reference
      EXCEPT SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId'))
        FROM OPENJSON(@ReplacementCanonical,'$.duplicateResolutions'))
      OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId'))
        FROM OPENJSON(@ReplacementCanonical,'$.duplicateResolutions')
        EXCEPT SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND SupplierId=@Supplier AND NormalizedReference=@Reference)
      THROW 51009,'Successor credit reference requires its exact recorded resolution.',1;
    EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@Evidence,@Documents OUTPUT;
    SET @Claims=JSON_QUERY(@ReplacementCanonical,'$.claims');
    IF EXISTS(SELECT 1 FROM OPENJSON(@Claims) claim WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierBillPostings bill
      JOIN Purchasing.SupplierBills owner ON owner.TenantId=bill.TenantId AND owner.Id=bill.BillId AND owner.State='Posted'
        AND owner.SupplierId=@Supplier AND owner.PurchaseOrderId=@Po AND owner.Currency=@Currency
      JOIN Purchasing.SupplierBillPostingEvents owned ON owned.TenantId=bill.TenantId AND owned.BillId=bill.BillId
        AND owned.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId'))
      WHERE bill.TenantId=@TenantId AND bill.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billId'))
        AND bill.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billRevisionId'))))
      THROW 51004,'Replacement claims require their actual original bill provenance.',1;
    IF (SELECT SUM(CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))) FROM OPENJSON(@Claims))
      <>CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.total'))
      THROW 51000,'Replacement credit claims must equal its total.',1;
    UPDATE @Resolved SET RequestedAllocations=JSON_QUERY(@ReplacementCanonical,'$.allocations'),
      Replacement=JSON_MODIFY(@ReplacementCanonical,'$.allocations',JSON_QUERY(N'[]')),
      ReplacementId=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditId')),
      ReplacementRevisionId=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditRevisionId')) WHERE Id=@Id;
    FETCH NEXT FROM replacements INTO @Id,@Replacement,@ChildDate;
  END;
  CLOSE replacements; DEALLOCATE replacements;
  IF EXISTS(SELECT ReplacementId FROM @Resolved WHERE ReplacementId IS NOT NULL GROUP BY ReplacementId HAVING COUNT(*)>1)
    OR EXISTS(SELECT ReplacementRevisionId FROM @Resolved WHERE ReplacementRevisionId IS NOT NULL GROUP BY ReplacementRevisionId HAVING COUNT(*)>1)
    THROW 51000,'Each replacement requires its own fresh source and revision.',1;
  IF EXISTS(SELECT Purchasing.NormalizeBillReference(JSON_VALUE(Replacement,'$.reference')) FROM @Resolved
    WHERE Replacement IS NOT NULL GROUP BY Purchasing.NormalizeBillReference(JSON_VALUE(Replacement,'$.reference')) HAVING COUNT(*)>1)
    THROW 51009,'Successor references must be distinct within the complete proposal.',1;
  SET @Sources=(SELECT Id sourceId,RevisionId sourceRevisionId,CONVERT(varchar(18),Version,1) version,
    CONVERT(nvarchar(60),Amount) amount,PostingDate sourcePostingDate,CorrectionDate postingDate,JournalId journalId,
    SourceEventId sourceEventId,CONVERT(varchar(64),SnapshotSha256,2) sourceSnapshotSha256,
    JSON_QUERY(Replacement) replacement,JSON_QUERY(RequestedAllocations) requestedAllocations,
    ReplacementId replacementId,ReplacementRevisionId replacementRevisionId
    FROM @Resolved ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES);
END;
