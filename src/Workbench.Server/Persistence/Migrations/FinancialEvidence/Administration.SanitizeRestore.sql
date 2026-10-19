-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Administration].[SanitizeRestore]
    @Now datetimeoffset,
    @CorrelationId nvarchar(100)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;
    UPDATE [Security].[DatabaseSecurityState] WITH (UPDLOCK, HOLDLOCK)
    SET [RestoreGeneration] = [RestoreGeneration] + 1
    WHERE [Id] = 1;
    DELETE FROM [ServiceAdministration].[Sessions];
    UPDATE [ServiceAdministration].[Accounts]
    SET [SecurityVersion] = [SecurityVersion] + 1;
    DELETE FROM [Identity].[Sessions];

    DELETE FROM [Operations].[WorkItems] WHERE [Kind] = 2;
    DELETE FROM [Identity].[IdentityOperations];
    UPDATE [Operations].[WorkItems] SET [State] = 0, [Attempts] = 0,
        [Generation] = [Generation] + 1, [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
        WHERE [State] IN (0,1);
    UPDATE [Security].[BlobRecoveryState] SET [IsPending] =
        CASE WHEN EXISTS (SELECT 1 FROM [Storage].[Revisions] WHERE [State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) THEN 1 ELSE 0 END
        WHERE [Id] = 1;

    DELETE FROM [Identity].[DataProtectionKeys];
    DELETE FROM [Security].[SensitiveRequestLimits];
    UPDATE [Identity].[Users]
    SET [SecurityVersion] = [SecurityVersion] + 1,
        [SecurityStamp] = CONVERT(nvarchar(36), NEWID()),
        [ConcurrencyStamp] = CONVERT(nvarchar(36), NEWID());
    UPDATE [Security].[DatabaseSecurityState]
    SET [RestoreSanitizedGeneration] = [RestoreGeneration], [SanitizedAtUtc] = @Now
    WHERE [Id] = 1;
    UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 0 WHERE [Id] = 1;
    INSERT INTO [Security].[SystemSecurityAuditEvents]
        ([Id], [Action], [Outcome], [CorrelationId], [MetadataJson], [OccurredAtUtc])
    VALUES
        (NEWID(), N'database.restore-sanitized', N'Succeeded', @CorrelationId,
         N'{"version":1}', @Now);
    COMMIT TRANSACTION;
END;
