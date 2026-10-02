// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class GemReferenceCurationSchema
{
    internal const string AssertAuthority = """
        DECLARE @Now datetimeoffset=SYSUTCDATETIME();
        IF NOT EXISTS(SELECT 1 FROM ServiceAdministration.Sessions s JOIN ServiceAdministration.Accounts a ON a.Id=s.AccountId
            WHERE s.Id=@SessionId AND a.Id=@AccountId AND a.IsEnabled=1 AND s.SecurityVersion=a.SecurityVersion
                AND s.RevokedAtUtc IS NULL AND s.IdleExpiresAtUtc>@Now AND s.AbsoluteExpiresAtUtc>@Now)
            THROW 50041,'Current service-admin authority is required.',1;
        """;

    internal static void Up(MigrationBuilder migration, string migrationId)
    {
        // Like GEM-03, private command-owned storage is deliberately outside the tenant EF context.
        migration.Sql("""
            CREATE TABLE Gemology.Drafts(
                Id uniqueidentifier NOT NULL PRIMARY KEY CHECK(Id<>'00000000-0000-0000-0000-000000000000'),
                EntryId uniqueidentifier NOT NULL CHECK(EntryId<>'00000000-0000-0000-0000-000000000000'),
                ContentJson nvarchar(max) NOT NULL CHECK(ISJSON(ContentJson)=1 AND DATALENGTH(ContentJson)<=2097152),
                ExpectedPublishedRowVersion binary(8) NULL,
                RowVersion rowversion NOT NULL,
                CreatedBy uniqueidentifier NOT NULL REFERENCES ServiceAdministration.Accounts(Id),
                UpdatedBy uniqueidentifier NOT NULL REFERENCES ServiceAdministration.Accounts(Id),
                CreatedAtUtc datetimeoffset NOT NULL,
                UpdatedAtUtc datetimeoffset NOT NULL);
            CREATE TABLE Gemology.PublishRequests(
                Id uniqueidentifier NOT NULL PRIMARY KEY CHECK(Id<>'00000000-0000-0000-0000-000000000000'),
                AccountId uniqueidentifier NOT NULL REFERENCES ServiceAdministration.Accounts(Id),
                SelectionJson nvarchar(max) NOT NULL CHECK(ISJSON(SelectionJson)=1),
                OutcomeJson nvarchar(max) NOT NULL CHECK(ISJSON(OutcomeJson)=1),
                CreatedAtUtc datetimeoffset NOT NULL);
            CREATE TABLE Gemology.PublicationAudit(
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                RequestId uniqueidentifier NOT NULL,
                AccountId uniqueidentifier NOT NULL REFERENCES ServiceAdministration.Accounts(Id),
                Outcome nvarchar(40) NOT NULL,
                SummaryJson nvarchar(max) NOT NULL CHECK(ISJSON(SummaryJson)=1),
                CreatedAtUtc datetimeoffset NOT NULL);
            """);
        foreach (var table in new[] { "Drafts", "PublishRequests", "PublicationAudit" })
        foreach (var role in new[] { "workbench_web", "workbench_worker", "workbench_operator" })
            migration.Sql($"DENY SELECT,INSERT,UPDATE,DELETE ON Gemology.{table} TO {role};");

        Procedure(migration, "ReadDrafts", "@AccountId uniqueidentifier,@SessionId uniqueidentifier,@AfterId uniqueidentifier=NULL", """
            SELECT TOP(51) * FROM Gemology.Drafts WHERE @AfterId IS NULL OR Id>@AfterId ORDER BY Id;
            """);
        Procedure(migration, "ReadDraft", "@AccountId uniqueidentifier,@SessionId uniqueidentifier,@DraftId uniqueidentifier", "SELECT * FROM Gemology.Drafts WHERE Id=@DraftId;");
        Procedure(migration, "SaveDraft", """
            @AccountId uniqueidentifier,@SessionId uniqueidentifier,@DraftId uniqueidentifier,@EntryId uniqueidentifier,
            @ContentJson nvarchar(max),@ExpectedDraftRowVersion varbinary(max)=NULL,@ExpectedPublishedRowVersion varbinary(max)=NULL
            """, """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @Lock int;
            EXEC @Lock=sys.sp_getapplock @Resource=N'Gemology.Publication',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Lock<0 THROW 50042,'Catalog is busy. Retry the request.',1;
            IF @DraftId='00000000-0000-0000-0000-000000000000' OR @EntryId='00000000-0000-0000-0000-000000000000'
                OR ISJSON(@ContentJson)<>1 OR DATALENGTH(@ContentJson)>2097152
                OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ContentJson,'$.id'))<>@EntryId
                OR JSON_VALUE(@ContentJson,'$.id') IS NULL
                OR (@ExpectedDraftRowVersion IS NOT NULL AND DATALENGTH(@ExpectedDraftRowVersion)<>8)
                OR (@ExpectedPublishedRowVersion IS NOT NULL AND DATALENGTH(@ExpectedPublishedRowVersion)<>8)
                THROW 50043,'Invalid draft.',1;
            IF (@ExpectedPublishedRowVersion IS NULL AND EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=@EntryId))
                OR (@ExpectedPublishedRowVersion IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=@EntryId AND RowVersion=@ExpectedPublishedRowVersion))
                THROW 50044,'Published entry changed. Review and explicitly rebase the draft.',1;
            IF EXISTS(SELECT 1 FROM Gemology.Drafts WHERE Id=@DraftId)
            BEGIN
                UPDATE Gemology.Drafts SET ContentJson=@ContentJson,ExpectedPublishedRowVersion=@ExpectedPublishedRowVersion,
                    UpdatedBy=@AccountId,UpdatedAtUtc=@Now WHERE Id=@DraftId AND EntryId=@EntryId AND RowVersion=@ExpectedDraftRowVersion;
                IF @@ROWCOUNT<>1 THROW 50044,'Draft changed. Reload before saving.',1;
            END
            ELSE
            BEGIN
                IF @ExpectedDraftRowVersion IS NOT NULL THROW 50044,'Draft no longer exists.',1;
                INSERT Gemology.Drafts(Id,EntryId,ContentJson,ExpectedPublishedRowVersion,CreatedBy,UpdatedBy,CreatedAtUtc,UpdatedAtUtc)
                    VALUES(@DraftId,@EntryId,@ContentJson,@ExpectedPublishedRowVersion,@AccountId,@AccountId,@Now,@Now);
            END;
            SELECT * FROM Gemology.Drafts WHERE Id=@DraftId;
            COMMIT;
            """);
        Procedure(migration, "ReadPublication", "@AccountId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier", "SELECT SelectionJson,OutcomeJson FROM Gemology.PublishRequests WHERE Id=@RequestId AND AccountId=@AccountId;");
        Procedure(migration, "ReadPublicationAudit", "@AccountId uniqueidentifier,@SessionId uniqueidentifier,@AfterId uniqueidentifier=NULL", "SELECT TOP(51) * FROM Gemology.PublicationAudit WHERE @AfterId IS NULL OR Id>@AfterId ORDER BY Id;");
        Procedure(migration, "PublishDraftBatch", "@AccountId uniqueidentifier,@SessionId uniqueidentifier", "THROW 50043,'Publication payload is required.',1;");
        migration.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20261001072507_AddServiceAdminIdentity',@Definition)=0
                THROW 50020,'Unsupported catalog curation readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20261001072507_AddServiceAdminIdentity',N'{migrationId}');
            EXEC sys.sp_executesql @Definition;
            """);
    }

    internal static void Procedure(MigrationBuilder migration, string name, string parameters, string body)
    {
        migration.Sql($"CREATE PROCEDURE Gemology.{name} {parameters} AS BEGIN SET NOCOUNT ON; {AssertAuthority} {body} END;");
        migration.Sql($"GRANT EXECUTE ON Gemology.{name} TO workbench_web;");
    }
}
