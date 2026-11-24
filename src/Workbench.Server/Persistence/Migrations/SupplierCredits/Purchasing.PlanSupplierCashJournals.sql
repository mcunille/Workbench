-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierCashJournals
  @TenantId uniqueidentifier,@Date date,@Currency varchar(3),@Sources nvarchar(max),@Targets nvarchar(max),@Journals nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Members TABLE(MemberKind varchar(40),SourceKind varchar(32),SourceId uniqueidentifier,
    SourceRevisionId uniqueidentifier,OriginalJournalId uniqueidentifier,OwnerMemberId uniqueidentifier,Position int,Lines nvarchar(max));
  INSERT @Members(MemberKind,SourceKind,SourceId,SourceRevisionId,OriginalJournalId,Lines)
    SELECT 'ReverseSource',source.SourceKind,source.SourceId,source.SourceRevisionId,source.JournalId,
      (SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,
        CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit
        FROM Accounting.JournalInverseLines(@TenantId,source.JournalId) ORDER BY Ordinal FOR JSON PATH)
    FROM OPENJSON(@Sources) WITH(SourceKind varchar(32) '$.sourceKind',SourceId uniqueidentifier '$.sourceId',
      SourceRevisionId uniqueidentifier '$.sourceRevisionId',JournalId uniqueidentifier '$.journalId') source;

  DECLARE @Kind varchar(32),@Id uniqueidentifier,@Revision uniqueidentifier,@Funding uniqueidentifier,
    @Version uniqueidentifier,@Amount decimal(28,4),@ResolvedTargets nvarchar(max),@Lines nvarchar(max);
  DECLARE replacements CURSOR LOCAL FAST_FORWARD FOR
    SELECT SourceKind,ReplacementId,ReplacementRevision,FundingId,FundingVersion,Amount FROM OPENJSON(@Sources)
    WITH(SourceKind varchar(32) '$.sourceKind',ReplacementId uniqueidentifier '$.replacementId',
      ReplacementRevision uniqueidentifier '$.replacementRevisionId',FundingId uniqueidentifier '$.replacement.fundingAccountId',
      FundingVersion uniqueidentifier '$.replacement.expectedFundingAccountVersion',Amount decimal(28,4) '$.replacementAmount')
    WHERE ReplacementId IS NOT NULL;
  OPEN replacements;
  FETCH NEXT FROM replacements INTO @Kind,@Id,@Revision,@Funding,@Version,@Amount;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @ResolvedTargets=(SELECT AccountId accountId,AccountVersion accountVersion,Amount amount FROM OPENJSON(@Targets)
      WITH(SourceId uniqueidentifier '$.sourceId',AccountId uniqueidentifier '$.accountId',
        AccountVersion uniqueidentifier '$.accountVersion',Amount decimal(28,4) '$.amount')
      WHERE SourceId=@Id FOR JSON PATH);
    EXEC Purchasing.BuildSupplierCashJournalLines @Kind,@Funding,@Version,@Amount,@ResolvedTargets,@Lines OUTPUT;
    INSERT @Members(MemberKind,SourceKind,SourceId,SourceRevisionId,Lines) VALUES('Replacement',@Kind,@Id,@Revision,@Lines);
    FETCH NEXT FROM replacements INTO @Kind,@Id,@Revision,@Funding,@Version,@Amount;
  END;
  CLOSE replacements; DEALLOCATE replacements;

  SET @Journals=(SELECT MemberKind memberKind,SourceKind sourceKind,SourceId sourceId,SourceRevisionId sourceRevisionId,
    OriginalJournalId originalJournalId,OwnerMemberId ownerMemberId,Position position,
    @Date postingDate,@Currency currency,JSON_QUERY(Lines) lines
    FROM @Members ORDER BY MemberKind,SourceKind,SourceId,SourceRevisionId,OriginalJournalId,OwnerMemberId,Position
    FOR JSON PATH,INCLUDE_NULL_VALUES);
END;
