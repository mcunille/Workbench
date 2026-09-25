// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class JournalControlAdapterSql
{
    internal const string Install = """
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
}
