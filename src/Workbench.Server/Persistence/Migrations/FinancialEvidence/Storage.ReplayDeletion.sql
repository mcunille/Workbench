-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Storage].[ReplayDeletion] @Id uniqueidentifier
WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @Tenant uniqueidentifier,@Attachment uniqueidentifier;
SELECT @Tenant=TenantId,@Attachment=AttachmentId FROM Operations.WorkItems WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Kind=1 AND State=3;
IF @Attachment IS NOT NULL EXEC Storage.RequireFinancialEvidenceDeletion @Tenant,@Attachment,1;
UPDATE [Operations].[WorkItems] SET [State] = 0, [Attempts] = 0,
        [Generation] = [Generation] + 1, [AvailableAtUtc] = SYSUTCDATETIME(), [Outcome] = NULL
    WHERE [Id] = @Id AND [Kind] = 1 AND [State] = 3;
    IF @@ROWCOUNT <> 1 THROW 50023, 'Deletion work is not replayable.', 1;
    INSERT INTO [Security].[SystemSecurityAuditEvents] ([Id], [Action], [Outcome], [OccurredAtUtc])
    VALUES (NEWID(), N'storage.deletion-replayed', N'Succeeded', SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
