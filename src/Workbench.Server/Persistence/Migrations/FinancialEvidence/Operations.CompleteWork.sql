-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Operations].[CompleteWork]
    @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @Tenant uniqueidentifier,@Attachment uniqueidentifier;
SELECT @Tenant=TenantId,@Attachment=AttachmentId FROM Operations.WorkItems WITH(UPDLOCK,HOLDLOCK)
WHERE Id=@Id AND LeaseOwner=@Owner AND Generation=@Generation AND State=1 AND LeaseExpiresAtUtc>SYSUTCDATETIME() AND Kind=1;
IF @Attachment IS NOT NULL EXEC Storage.RequireFinancialEvidenceDeletion @Tenant,@Attachment,1;
DECLARE @Completed TABLE ([TenantId] uniqueidentifier, [Kind] int, [AttachmentId] uniqueidentifier);
    UPDATE [Operations].[WorkItems]
    SET [State] = 2, [ProtectedPayload] = NULL, [Outcome] = N'Completed',
        [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
    OUTPUT inserted.[TenantId], inserted.[Kind], inserted.[AttachmentId] INTO @Completed
    WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
        AND [State] = 1 AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
    UPDATE r SET [State] = CASE WHEN r.[State] = 1 THEN 3 ELSE 2 END
    FROM [Storage].[Revisions] r JOIN @Completed c
        ON c.[TenantId] = r.[TenantId] AND c.[AttachmentId] = r.[AttachmentId]
    WHERE c.[Kind] = 1 AND r.[State] IN (0,1);
    INSERT INTO [Security].[TenantSecurityAuditEvents]
        ([Id], [TenantId], [Action], [TargetType], [TargetId], [Outcome], [OccurredAtUtc])
        SELECT NEWID(), [TenantId], N'operations.work.completed', N'WorkItem', @Id,
            N'Succeeded', SYSUTCDATETIME() FROM @Completed;
    COMMIT;
    SELECT COUNT(*) FROM @Completed;
END;
