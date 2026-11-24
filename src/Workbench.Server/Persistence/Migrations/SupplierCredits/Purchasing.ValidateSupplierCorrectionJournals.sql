-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateSupplierCorrectionJournals
  @TenantId uniqueidentifier,@GroupId uniqueidentifier,@Plan nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  IF JSON_QUERY(@Plan,'$.proposedJournals') IS NULL
    THROW 51004,'The approved journal set is unavailable.',1;
  DECLARE @Expected TABLE(Id int PRIMARY KEY,MemberKind varchar(40),SourceKind varchar(40),SourceId uniqueidentifier,
    SourceRevisionId uniqueidentifier,OriginalJournalId uniqueidentifier,OwnerMemberId uniqueidentifier,Position int,
    PostingDate date,Currency varchar(3),Lines nvarchar(max));
  INSERT @Expected SELECT CONVERT(int,proposed.[key]),expected.*
    FROM OPENJSON(@Plan,'$.proposedJournals') proposed CROSS APPLY OPENJSON(proposed.value)
    WITH(MemberKind varchar(40) '$.memberKind',SourceKind varchar(40) '$.sourceKind',SourceId uniqueidentifier '$.sourceId',
      SourceRevisionId uniqueidentifier '$.sourceRevisionId',OriginalJournalId uniqueidentifier '$.originalJournalId',
      OwnerMemberId uniqueidentifier '$.ownerMemberId',Position int '$.position',PostingDate date '$.postingDate',
      Currency varchar(3) '$.currency',Lines nvarchar(max) '$.lines' AS JSON) expected;

  DECLARE @Reapplications TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4));
  INSERT @Reapplications SELECT CONVERT(int,proposed.[key]),target.*
    FROM OPENJSON(@Plan,'$.reapplications') proposed CROSS APPLY OPENJSON(proposed.value)
    WITH(FundingId uniqueidentifier '$.fundingItemId',DebtId uniqueidentifier '$.debtItemId',Amount decimal(28,4) '$.amount') target;
  DECLARE @InvertedApplications TABLE(ReversalId uniqueidentifier PRIMARY KEY,ApplicationId uniqueidentifier,OriginalJournalId uniqueidentifier);
  INSERT @InvertedApplications SELECT reversed.Id,reversed.ApplicationId,control.JournalId FROM Purchasing.SupplierApplicationReversals reversed
    CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,reversed.ApplicationId) control
    WHERE reversed.TenantId=@TenantId AND reversed.GroupId=@GroupId;

  DECLARE @Actual TABLE(JournalId uniqueidentifier,MemberKind varchar(40),SourceKind varchar(40),SourceId uniqueidentifier,
    SourceRevisionId uniqueidentifier,OriginalJournalId uniqueidentifier,OwnerMemberId uniqueidentifier,Position int,PostingDate date,Currency varchar(3));
  INSERT @Actual SELECT journal.Id,member.Kind,source.SourceKind,
    CASE member.Kind WHEN 'Application' THEN applied.FundingItemId WHEN 'ReverseApplication' THEN NULL
      WHEN 'RecognitionAdjustment' THEN TRY_CONVERT(uniqueidentifier,JSON_VALUE(member.EffectsJson,'$.sourceId')) ELSE source.SourceId END,
    CASE member.Kind WHEN 'ReverseSource' THEN originalSource.SourceRevision WHEN 'Replacement' THEN source.SourceRevision END,
    COALESCE(inverse.OriginalJournalId,invertedApplication.OriginalJournalId),
    CASE member.Kind WHEN 'Application' THEN applied.DebtItemId WHEN 'ReverseApplication' THEN invertedApplication.ApplicationId
      WHEN 'ReleaseSettlement' THEN member.EntityId WHEN 'ReverseSource' THEN COALESCE(member.RecognitionEventId,
        CASE WHEN originalSource.SourceKind='SupplierRefundRelease' THEN originalSource.SourceId END)
      ELSE member.RecognitionEventId END,
    target.Position,journal.PostingDate,journal.Currency
    FROM Purchasing.SupplierSourceCorrectionMembers member
    JOIN Purchasing.SupplierSourceCorrections owner ON owner.TenantId=member.TenantId AND owner.Id=member.CorrectionId
      AND owner.GroupId=member.GroupId
    JOIN Accounting.JournalEntries journal ON journal.TenantId=member.TenantId AND journal.Id=member.JournalId
    JOIN Accounting.SourceEvents source ON source.TenantId=journal.TenantId AND source.Id=journal.SourceEventId
    LEFT JOIN Accounting.CorrectionGroups inverse ON inverse.TenantId=member.TenantId AND inverse.ReversalJournalId=member.JournalId
      AND member.Kind='ReverseSource'
    LEFT JOIN Accounting.JournalEntries originalJournal ON originalJournal.TenantId=inverse.TenantId AND originalJournal.Id=inverse.OriginalJournalId
    LEFT JOIN Accounting.SourceEvents originalSource ON originalSource.TenantId=originalJournal.TenantId AND originalSource.Id=originalJournal.SourceEventId
    LEFT JOIN @InvertedApplications invertedApplication ON invertedApplication.ReversalId=source.SourceId
      AND invertedApplication.ApplicationId=member.EntityId AND member.Kind='ReverseApplication'
    LEFT JOIN Purchasing.SupplierApplications applied ON applied.TenantId=member.TenantId AND applied.GroupId=member.GroupId
      AND applied.Id=member.EntityId AND applied.Id=source.SourceId AND member.Kind='Application'
    LEFT JOIN @Reapplications target ON target.Position=TRY_CONVERT(int,JSON_VALUE(member.EffectsJson,'$.position'))
      AND target.FundingId=applied.FundingItemId AND target.DebtId=applied.DebtItemId AND target.Amount=applied.Amount
    WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND (
      (member.Kind='ReverseSource' AND source.SourceId=originalSource.SourceId AND source.SourceKind=originalSource.SourceKind
        AND member.EntityId=CASE WHEN originalSource.SourceKind IN('SupplierRefundRelease','SupplierRecognitionCreditAdjustment')
          THEN originalJournal.Id ELSE originalSource.SourceId END
        AND (originalSource.SourceKind NOT IN('SupplierCredit','SupplierRefund','SupplierRefundRepayment')
          OR (owner.SourceKind=originalSource.SourceKind AND owner.SourceId=originalSource.SourceId)))
      OR (member.Kind='ReverseApplication' AND invertedApplication.ReversalId IS NOT NULL AND source.SourceRevision=@GroupId)
      OR (member.Kind='Application' AND target.Position IS NOT NULL AND source.SourceRevision=@GroupId)
      OR (member.Kind='Replacement' AND source.SourceId=member.EntityId
        AND (source.SourceKind='PurchaseRecognition' OR
          (owner.SourceKind=source.SourceKind AND owner.ReplacementSourceId=source.SourceId AND owner.ReplacementSourceRevisionId=source.SourceRevision)))
      OR (member.Kind='ReleaseSettlement' AND source.SourceId=member.EntityId AND source.SourceRevision=member.CorrectionId)
      OR (member.Kind='RecognitionAdjustment' AND source.SourceId=member.EntityId AND source.SourceRevision=member.CorrectionId
        AND owner.SourceKind='SupplierCredit' AND owner.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.SnapshotJson,'$.adjustment.sourceId'))
        AND member.RecognitionEventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.SnapshotJson,'$.adjustment.recognitionEventId'))
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.SnapshotJson,'$.groupId'))=member.GroupId
        AND CONVERT(varbinary(max),JSON_QUERY(source.SnapshotJson,'$.adjustment'))=CONVERT(varbinary(max),member.EffectsJson))
    );

  DECLARE @Matches TABLE(ExpectedId int,JournalId uniqueidentifier);
  INSERT @Matches SELECT expected.Id,actual.JournalId FROM @Expected expected JOIN @Actual actual
    ON actual.MemberKind=expected.MemberKind AND actual.SourceKind=expected.SourceKind
    AND actual.PostingDate=expected.PostingDate AND actual.Currency=expected.Currency
    AND EXISTS(SELECT actual.SourceId,actual.SourceRevisionId,actual.OriginalJournalId,actual.OwnerMemberId,actual.Position
      INTERSECT SELECT expected.SourceId,expected.SourceRevisionId,expected.OriginalJournalId,expected.OwnerMemberId,expected.Position);
  DECLARE @ExpectedCount int=(SELECT COUNT(*) FROM @Expected);
  IF @ExpectedCount<>(SELECT COUNT(*) FROM @Actual)
    OR @ExpectedCount<>(SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrectionMembers
      WHERE TenantId=@TenantId AND GroupId=@GroupId AND JournalId IS NOT NULL)
    OR EXISTS(SELECT ExpectedId FROM @Matches GROUP BY ExpectedId HAVING COUNT(*)<>1)
    OR EXISTS(SELECT JournalId FROM @Matches GROUP BY JournalId HAVING COUNT(*)<>1)
    OR @ExpectedCount<>(SELECT COUNT(*) FROM @Matches)
    THROW 51004,'The posted members differ from the approved journal set.',1;

  DECLARE @ExpectedLines TABLE(JournalId uniqueidentifier,Ordinal int,AccountId uniqueidentifier,AccountVersion uniqueidentifier,
    Debit decimal(28,4),Credit decimal(28,4));
  INSERT @ExpectedLines SELECT matched.JournalId,line.*
    FROM @Matches matched JOIN @Expected expected ON expected.Id=matched.ExpectedId
    CROSS APPLY OPENJSON(expected.Lines) WITH(Ordinal int '$.ordinal',AccountId uniqueidentifier '$.accountId',
      AccountVersion uniqueidentifier '$.accountVersion',Debit decimal(28,4) '$.debit',Credit decimal(28,4) '$.credit') line;
  DECLARE @ActualLines TABLE(JournalId uniqueidentifier,Ordinal int,AccountId uniqueidentifier,AccountVersion uniqueidentifier,
    Debit decimal(28,4),Credit decimal(28,4));
  INSERT @ActualLines SELECT line.JournalId,line.Ordinal,line.AccountId,line.AccountVersion,line.Debit,line.Credit
    FROM Accounting.JournalLines line JOIN @Matches matched ON matched.JournalId=line.JournalId WHERE line.TenantId=@TenantId;
  IF (SELECT COUNT(*) FROM @ExpectedLines)<>(SELECT COUNT(*) FROM @ActualLines)
    OR EXISTS(SELECT * FROM @ExpectedLines EXCEPT SELECT * FROM @ActualLines)
    OR EXISTS(SELECT * FROM @ActualLines EXCEPT SELECT * FROM @ExpectedLines)
    THROW 51004,'The posted lines differ from the approved journal set.',1;
END;
