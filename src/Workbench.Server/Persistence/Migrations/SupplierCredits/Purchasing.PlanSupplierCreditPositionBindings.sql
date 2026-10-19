-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierCreditPositionBindings
  @TenantId uniqueidentifier,@Sources nvarchar(max),@Bindings nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Position binding requires its complete source owner transaction.',1;
  -- A newly matched side can change Prepayment to Expense without changing numeric
  -- capacity. Both bill and credit planners bind the actual current historical position.
  SET @Bindings=(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId')) creditId,
    position.RecognitionEventId recognitionEventId,position.UnitId unitId,position.ComponentKey componentKey,position.Kind componentKind,
    JSON_VALUE(claim.value,'$.amount') amount,JSON_VALUE(source.value,'$.replacement.postingDate') postingDate,
    position.Treatment treatment,position.AccountId accountId,position.AccountVersion accountVersion,
    position.OriginalJournalId originalJournalId,position.OriginalJournalOrdinal originalJournalOrdinal,
    CONVERT(varchar(64),invoice.EvidenceSha256,2) invoiceEvidenceSha256,
    matched.Id matchId,matched.RecognitionId matchedRecognitionEventId,matched.EvidenceHash matchedEvidenceSha256
    FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.replacement.claims') claim
    CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')),
      JSON_VALUE(claim.value,'$.componentKey'),TRY_CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23)) position
    JOIN Purchasing.RecognitionSideEvents invoice WITH(UPDLOCK,HOLDLOCK) ON invoice.TenantId=@TenantId AND invoice.Id=position.RecognitionEventId
    OUTER APPLY(SELECT match.Id,recognition.Id RecognitionId,CONVERT(varchar(64),recognition.EvidenceSha256,2) EvidenceHash
      FROM Purchasing.RecognitionMatches match JOIN Purchasing.ActiveRecognitionSideEvents recognition
        ON recognition.TenantId=match.TenantId AND recognition.Id=match.RecognitionEventId
      WHERE match.TenantId=@TenantId AND match.InvoiceEventId=position.RecognitionEventId
        AND recognition.PostingDate<=TRY_CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23)) matched
    ORDER BY JSON_VALUE(source.value,'$.replacementId'),position.RecognitionEventId,position.ComponentKey
    FOR JSON PATH,INCLUDE_NULL_VALUES);
  IF (SELECT COUNT(*) FROM OPENJSON(@Bindings))<>(SELECT COUNT(*) FROM OPENJSON(@Sources) source
      CROSS APPLY OPENJSON(source.value,'$.replacement.claims') claim)
    THROW 51004,'Complete current claim position bindings are required.',1;
END;
