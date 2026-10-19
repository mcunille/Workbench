  IF @ReplacementSourceRevision IS NOT NULL
  BEGIN
    SET @ReplacementRequestId=NEWID();
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    INSERT @Posted EXEC Accounting.PostJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@ReplacementRequestId,
      @RequiredPermission=@RequiredPermission,@SourceCommandKind=N'Correction.InternalReplacement',@SourceCommandVersion=1,
      @CanonicalInput=@InternalInput,@SourceKind=@SourceKind,@SourceId=@SourceId,@SourceRevision=@ReplacementSourceRevision,
      @EventKind=@EventKind,@RuleVersion=@ReplacementRuleVersion,@ExpectedConfigurationVersion=@ExpectedConfigurationVersion,
      @Currency=@Currency,@DocumentDate=@ReplacementDocumentDate,@EffectiveDate=@EffectiveDate,@PostingDate=@PostingDate,
      @Reference=@Reference,@Reason=@Reason,@SourceSnapshot=@ReplacementSnapshot,@Lines=@ReplacementLines;
    SELECT @ReplacementEventId=SourceEventId,@ReplacementJournalId=JournalId FROM @Posted;
    -- These are this transaction's new, uncommitted rows, never previously recorded history.
    -- One database-owned instant prevents as-recorded cutoffs from showing a half-correction.
    UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now
      WHERE TenantId=@TenantId AND Id=@ReplacementEventId;
    UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now
      WHERE TenantId=@TenantId AND Id=@ReplacementJournalId;
    UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now
      WHERE TenantId=@TenantId AND RequestId=@ReplacementRequestId;
  END;
  INSERT Accounting.CorrectionGroups(TenantId,Id,OriginalSourceEventId,OriginalJournalId,
    ReversalSourceEventId,ReversalJournalId,ReversalRequestId,ReplacementSourceEventId,ReplacementJournalId,
    ReplacementRequestId,ReplacementSourceRevision,ActorId,PostingDate,Reason,EvidenceJson,EvidenceSha256,RecordedAtUtc)
    VALUES(@TenantId,@CorrectionId,@OriginalEventId,@OriginalJournalId,@ReversalEventId,@ReversalJournalId,@ReversalRequestId,
      @ReplacementEventId,@ReplacementJournalId,@ReplacementRequestId,@ReplacementSourceRevision,
      @ActorId,@PostingDate,@Reason,@Evidence,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Evidence)),@Now);
  INSERT Accounting.CorrectionReceipts(TenantId,RequestId,ActorId,SourceCommandKind,SourceCommandVersion,
    CanonicalInput,InputSha256,CorrectionId)
    VALUES(@TenantId,@RequestId,@ActorId,@SourceCommandKind,@SourceCommandVersion,@CanonicalInput,
      HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInput)),@CorrectionId);
  INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
    VALUES(NEWID(),@TenantId,@ActorId,N'Accounting.CorrectJournal',N'JournalCorrection',@CorrectionId,@Now);
  SELECT @CorrectionId CorrectionId,@ReversalJournalId ReversalJournalId,@ReplacementJournalId ReplacementJournalId,
    @ReplacementSourceRevision ReplacementSourceRevision,@Now RecordedAtUtc;
END;
