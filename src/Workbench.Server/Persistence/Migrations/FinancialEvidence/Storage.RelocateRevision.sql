-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Storage].[RelocateRevision]
    @TenantId uniqueidentifier, @Id uniqueidentifier,
    @OldAlias nvarchar(64), @NewAlias nvarchar(64), @Length bigint, @Sha256 varchar(64)
WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @NewAlias IS NULL OR LEN(@NewAlias) = 0 THROW 50023, 'A provider alias is required.', 1;
    UPDATE [Storage].[Revisions] SET [ProviderAlias] = @NewAlias
    WHERE [TenantId] = @TenantId AND [Id] = @Id AND [ProviderAlias] = @OldAlias
        AND ([State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) AND [Length] = @Length AND [Sha256] = @Sha256;
    IF @@ROWCOUNT <> 1 THROW 50023, 'Revision changed during migration.', 1;
    INSERT INTO [Security].[SystemSecurityAuditEvents] ([Id], [Action], [Outcome], [OccurredAtUtc])
    VALUES (NEWID(), N'storage.provider-migrated', N'Succeeded', SYSUTCDATETIME());
END;
