// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class JournalControlAdapterSql
{
    internal static readonly string Install = CloseInstall + RevisionInstall + Create(CorrectionProcedure)
        + Create(RevisionTrigger) + Create(PostingFaultTrigger) + Create(GroupFaultTrigger) + Create(ReceiptFaultTrigger)
        + "GRANT EXECUTE ON Accounting.CorrectSyntheticJournal TO workbench_web;";

    private static string Create(string sql) => "EXEC(N'" + sql.Replace("'", "''", StringComparison.Ordinal) + "');";

    private const string CloseInstall = """
        EXEC(N'CREATE PROCEDURE Accounting.CloseSyntheticPeriod
            @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
            @ExpectedConfigurationVersion uniqueidentifier,@PeriodStart date,@Reason nvarchar(max),
            @Evidence nvarchar(max)=NULL
        WITH EXECUTE AS OWNER
        AS BEGIN
            SET NOCOUNT ON; SET XACT_ABORT ON;
            BEGIN TRY
              BEGIN TRANSACTION;
              IF @Evidence IS NULL SET @Evidence=(SELECT 1 schemaVersion,N''SyntheticReconciliation'' kind,
                  @PeriodStart periodStart FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              DECLARE @CanonicalInput nvarchar(max)=(SELECT @PeriodStart periodStart,
                  @ExpectedConfigurationVersion expectedConfigurationVersion,@Reason reason,
                  @Evidence evidence FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              EXEC Accounting.ClosePeriod @ActorId=@ActorId,@SessionId=@SessionId,
                  @RequestId=@RequestId,@ExpectedConfigurationVersion=@ExpectedConfigurationVersion,
                  @PeriodStart=@PeriodStart,@Reason=@Reason,@Evidence=@Evidence,
                  @CanonicalInput=@CanonicalInput;
              COMMIT;
            END TRY
            BEGIN CATCH
              IF @@TRANCOUNT>0 ROLLBACK;
              THROW;
            END CATCH;
        END');
        GRANT EXECUTE ON Accounting.CloseSyntheticPeriod TO workbench_web;
        """;

    private const string RevisionInstall = """
        ALTER TABLE Accounting.SyntheticSources ADD HasDependencies bit NOT NULL DEFAULT 0;
        CREATE TABLE Accounting.SyntheticSourceRevisions(
          TenantId uniqueidentifier NOT NULL,SourceId uniqueidentifier NOT NULL,Revision uniqueidentifier NOT NULL,
          Amount decimal(28,4) NOT NULL,Currency nvarchar(3) NOT NULL,
          DebitAccountId uniqueidentifier NOT NULL,CreditAccountId uniqueidentifier NOT NULL,
          PRIMARY KEY(TenantId,SourceId,Revision),
          FOREIGN KEY(TenantId,SourceId) REFERENCES Accounting.SyntheticSources(TenantId,Id));
        EXEC(N'ALTER SECURITY POLICY Security.TenantIsolationPolicy
          ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSourceRevisions,
          ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSourceRevisions AFTER INSERT,
          ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSourceRevisions AFTER UPDATE');
        EXEC(N'GRANT SELECT ON Accounting.SyntheticSourceRevisions TO workbench_web;
        DENY INSERT,UPDATE,DELETE ON Accounting.SyntheticSourceRevisions TO workbench_web;');
        """;

    private const string RevisionTrigger = """
        CREATE TRIGGER Accounting.RetainSyntheticRevision ON Accounting.SyntheticSources AFTER INSERT,UPDATE AS
        BEGIN
          SET NOCOUNT ON;
          INSERT Accounting.SyntheticSourceRevisions(TenantId,SourceId,Revision,Amount,Currency,DebitAccountId,CreditAccountId)
            SELECT TenantId,Id,Revision,Amount,Currency,DebitAccountId,CreditAccountId FROM inserted i
            WHERE NOT EXISTS(SELECT 1 FROM Accounting.SyntheticSourceRevisions r
              WHERE r.TenantId=i.TenantId AND r.SourceId=i.Id AND r.Revision=i.Revision);
        END;
        """;

    private const string PostingFaultTrigger = """
        CREATE TRIGGER Accounting.SyntheticCorrectionPostingFault ON Accounting.PostingReceipts AFTER INSERT AS
        BEGIN
          IF (TRY_CONVERT(int,SESSION_CONTEXT(N'SyntheticCorrectionFailpoint'))=1
                AND EXISTS(SELECT 1 FROM inserted WHERE SourceCommandKind=N'Correction.InternalReversal'))
            OR (TRY_CONVERT(int,SESSION_CONTEXT(N'SyntheticCorrectionFailpoint'))=2
                AND EXISTS(SELECT 1 FROM inserted WHERE SourceCommandKind=N'Correction.InternalReplacement'))
            THROW 51000,'Injected correction append failure.',1;
        END;
        """;
    private const string GroupFaultTrigger = """
        CREATE TRIGGER Accounting.SyntheticCorrectionGroupFault ON Accounting.CorrectionGroups AFTER INSERT AS
        BEGIN
          IF TRY_CONVERT(int,SESSION_CONTEXT(N'SyntheticCorrectionFailpoint'))=3
            THROW 51000,'Injected correction evidence failure.',1;
        END;
        """;
    private const string ReceiptFaultTrigger = """
        CREATE TRIGGER Accounting.SyntheticCorrectionReceiptFault ON Accounting.CorrectionReceipts AFTER INSERT AS
        BEGIN
          IF TRY_CONVERT(int,SESSION_CONTEXT(N'SyntheticCorrectionFailpoint'))=4
            THROW 51000,'Injected correction receipt failure.',1;
        END;
        """;

    private const string CorrectionProcedure = """
        CREATE PROCEDURE Accounting.CorrectSyntheticJournal
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @OriginalJournalId uniqueidentifier,@ExpectedConfigurationVersion uniqueidentifier,
          @PostingDate date,@ReplacementAmount nvarchar(max),@Reason nvarchar(max),@Failpoint int=0,
          @ExpectedSourceRevision uniqueidentifier=NULL,@ExpectedDebitVersion uniqueidentifier=NULL,
          @ExpectedCreditVersion uniqueidentifier=NULL,@ReplacementDocumentDate date=NULL,@EvidenceOverride nvarchar(max)=NULL
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingConfigurationManage';
            END TRY
            BEGIN CATCH
              IF ERROR_NUMBER()=50903 THROW 51003,'Current synthetic correction authority is required.',1;
              THROW;
            END CATCH;
            IF @Failpoint NOT BETWEEN 0 AND 4 THROW 51000,'Invalid synthetic failpoint.',1;
            EXEC sys.sp_set_session_context @key=N'SyntheticCorrectionFailpoint',@value=@Failpoint;
            DECLARE @CanonicalInput nvarchar(max)=(SELECT @OriginalJournalId originalJournalId,
              @ExpectedConfigurationVersion expectedConfigurationVersion,@PostingDate postingDate,
              @ReplacementAmount replacementAmount,@Reason reason,@ExpectedSourceRevision expectedSourceRevision,
              @ExpectedDebitVersion expectedDebitVersion,@ExpectedCreditVersion expectedCreditVersion,
              @ReplacementDocumentDate replacementDocumentDate,@EvidenceOverride evidenceOverride
              FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            DECLARE @ReplacementRevision uniqueidentifier=NULL,@Lines nvarchar(max)=NULL,@Snapshot nvarchar(max)=NULL,
              @Evidence nvarchar(max)=NULL,@RuleVersion int=NULL,@SourceId uniqueidentifier,@OriginalRevision uniqueidentifier,
              @OriginalDocumentDate date,@Digest binary(32),@Currency nvarchar(3),@DebitId uniqueidentifier,@CreditId uniqueidentifier;
            -- This check must precede source/current-account lookup and revision generation.
            IF NOT EXISTS(SELECT 1 FROM Accounting.CorrectionReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              SELECT @SourceId=e.SourceId,@OriginalRevision=e.SourceRevision,@Digest=e.SnapshotSha256,
                @OriginalDocumentDate=e.DocumentDate,@Currency=s.Currency,@DebitId=s.DebitAccountId,@CreditId=s.CreditAccountId
                FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents e ON e.TenantId=j.TenantId AND e.Id=j.SourceEventId
                JOIN Accounting.SyntheticSources s WITH(UPDLOCK,HOLDLOCK) ON s.TenantId=e.TenantId AND s.Id=e.SourceId
                WHERE j.TenantId=@TenantId AND j.Id=@OriginalJournalId
                  AND e.SourceKind=N'Synthetic' AND e.EventKind=N'Posted';
              IF @SourceId IS NULL THROW 51004,'Independent synthetic original is unavailable.',1;
              IF EXISTS(SELECT 1 FROM Accounting.SyntheticSources WHERE TenantId=@TenantId AND Id=@SourceId AND HasDependencies=1)
                THROW 51000,'Synthetic dependency corrections are unsupported.',1;
              IF NOT EXISTS(SELECT 1 FROM Accounting.SyntheticSources WHERE TenantId=@TenantId AND Id=@SourceId
                    AND Revision=@OriginalRevision AND FrozenAtUtc IS NOT NULL)
                OR (@ExpectedSourceRevision IS NOT NULL AND @ExpectedSourceRevision<>@OriginalRevision)
                THROW 51009,'Synthetic source revision changed.',1;
              SET @Evidence=COALESCE(@EvidenceOverride,(SELECT 1 schemaVersion,
                CONVERT(nvarchar(36),@OriginalRevision) originalSourceRevision,
                CONVERT(varchar(64),@Digest,2) originalSnapshotSha256 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER));
              IF @ReplacementAmount IS NOT NULL
              BEGIN
                IF @DebitId=@CreditId THROW 51000,'Synthetic source requires two accounts.',1;
                DECLARE @DebitVersion uniqueidentifier,@CreditVersion uniqueidentifier;
                SELECT @DebitVersion=Version FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@DebitId
                  AND Purpose=N'General' AND ArchivedAtUtc IS NULL;
                SELECT @CreditVersion=Version FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@CreditId
                  AND Purpose=N'General' AND ArchivedAtUtc IS NULL;
                IF @DebitVersion IS NULL OR @CreditVersion IS NULL THROW 51004,'Synthetic accounts unavailable.',1;
                IF (@ExpectedDebitVersion IS NOT NULL AND @ExpectedDebitVersion<>@DebitVersion)
                  OR (@ExpectedCreditVersion IS NOT NULL AND @ExpectedCreditVersion<>@CreditVersion)
                  THROW 51009,'Synthetic account revision changed.',1;
                SET @ReplacementRevision=NEWID();
                SET @ReplacementDocumentDate=COALESCE(@ReplacementDocumentDate,@OriginalDocumentDate);
                SET @RuleVersion=1;
                SET @Lines=(SELECT v.Ordinal ordinal,v.AccountId accountId,v.AccountVersion accountVersion,v.Debit debit,v.Credit credit
                  FROM (VALUES(1,@DebitId,@DebitVersion,@ReplacementAmount,N'0'),
                    (2,@CreditId,@CreditVersion,N'0',@ReplacementAmount)) v(Ordinal,AccountId,AccountVersion,Debit,Credit)
                  ORDER BY v.Ordinal FOR JSON PATH);
                SET @Snapshot=(SELECT @SourceId sourceId,@ReplacementRevision sourceRevision,@ReplacementAmount amount,
                  @Currency currency,@DebitId debitAccountId,@CreditId creditAccountId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              END
              ELSE IF @ReplacementDocumentDate IS NOT NULL THROW 51000,'No replacement document is allowed for reversal only.',1;
            END;
            EXEC Accounting.CorrectJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@RequestId,
              @RequiredPermission=N'AccountingConfigurationManage',@SourceCommandKind=N'Synthetic.Correct',@SourceCommandVersion=1,
              @CanonicalInput=@CanonicalInput,@OriginalJournalId=@OriginalJournalId,@ExpectedConfigurationVersion=@ExpectedConfigurationVersion,
              @PostingDate=@PostingDate,@Reason=@Reason,@Evidence=@Evidence,@ReplacementSourceRevision=@ReplacementRevision,
              @ReplacementDocumentDate=@ReplacementDocumentDate,@ReplacementRuleVersion=@RuleVersion,
              @ReplacementSnapshot=@Snapshot,@ReplacementLines=@Lines;
            IF @SourceId IS NOT NULL
              INSERT Accounting.SyntheticSourceRevisions(TenantId,SourceId,Revision,Amount,Currency,DebitAccountId,CreditAccountId)
                SELECT r.TenantId,r.SourceId,e.SourceRevision,r.Amount,r.Currency,r.DebitAccountId,r.CreditAccountId
                FROM Accounting.CorrectionReceipts receipt
                JOIN Accounting.CorrectionGroups g ON g.TenantId=receipt.TenantId AND g.Id=receipt.CorrectionId
                JOIN Accounting.SourceEvents e ON e.TenantId=g.TenantId AND e.Id=g.ReversalSourceEventId
                JOIN Accounting.SyntheticSourceRevisions r ON r.TenantId=e.TenantId AND r.SourceId=e.SourceId AND r.Revision=@OriginalRevision
                WHERE receipt.TenantId=@TenantId AND receipt.RequestId=@RequestId;
            IF @ReplacementRevision IS NOT NULL
            BEGIN
              UPDATE Accounting.SyntheticSources SET Revision=@ReplacementRevision,Amount=CONVERT(decimal(28,4),@ReplacementAmount)
                WHERE TenantId=@TenantId AND Id=@SourceId AND Revision=@OriginalRevision;
              IF @@ROWCOUNT<>1 THROW 51009,'Synthetic source changed during correction.',1;
            END;
            EXEC sys.sp_set_session_context @key=N'SyntheticCorrectionFailpoint',@value=NULL;
            COMMIT;
          END TRY
          BEGIN CATCH
            IF @@TRANCOUNT>0 ROLLBACK;
            EXEC sys.sp_set_session_context @key=N'SyntheticCorrectionFailpoint',@value=NULL;
            THROW;
          END CATCH;
        END;
        """;
}
