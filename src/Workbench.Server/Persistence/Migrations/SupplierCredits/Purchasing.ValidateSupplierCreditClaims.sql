-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateSupplierCreditClaims
  @TenantId uniqueidentifier,@Claims nvarchar(max),@PostingDate date,@DerivedClaims nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.AssertSupplierCreditCapacity @TenantId,@Claims,@PostingDate;
  SET @DerivedClaims=(SELECT JSON_VALUE(c.value,'$.claimId') claimId,JSON_VALUE(c.value,'$.billId') billId,
    JSON_VALUE(c.value,'$.billRevisionId') billRevisionId,position.RecognitionEventId recognitionEventId,
    position.ComponentKey componentKey,JSON_VALUE(c.value,'$.amount') amount,position.Treatment treatment,
    position.AccountId accountId,position.AccountVersion accountVersion,position.OriginalJournalId journalId,
    position.OriginalJournalOrdinal ordinal FROM OPENJSON(@Claims) c
    CROSS APPLY Purchasing.SupplierCreditPosition(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.value,'$.recognitionEventId')),
      JSON_VALUE(c.value,'$.componentKey'),@PostingDate) position FOR JSON PATH);
END;
