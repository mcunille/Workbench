// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class GemReferenceTenantSchema
{
    private const string AuthorityAndLocks = """
        DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
        IF @TenantId IS NULL OR @ActorId IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@TenantId))
            THROW 50051,'Current tenant authority is required.',1;
        IF NOT EXISTS(SELECT 1 FROM [Identity].Users u WITH(HOLDLOCK)
            JOIN Tenancy.Tenants t WITH(HOLDLOCK) ON t.Id=u.TenantId
            WHERE u.TenantId=@TenantId AND u.Id=@ActorId AND u.State=1 AND t.IsEnabled=1)
            THROW 50051,'Active tenant membership is required.',1;
        DECLARE @TenantLock nvarchar(255)=N'Gemology.Tenant:'+LOWER(CONVERT(nvarchar(36),@TenantId));
        IF @@TRANCOUNT=0 OR XACT_STATE()<>1
            THROW 50052,'A locked caller transaction is required.',1;
        IF COALESCE(APPLOCK_MODE(N'public',N'Gemology.Publication',N'Transaction'),N'NoLock')<>N'Exclusive'
            OR COALESCE(APPLOCK_MODE(N'public',@TenantLock,N'Transaction'),N'NoLock')<>N'Exclusive'
            THROW 50052,'Publication then tenant exclusive locks are required.',1;
        IF @EntryId IS NULL OR @EntryId='00000000-0000-0000-0000-000000000000'
            OR (@ExpectedTenantRowVersion IS NOT NULL AND DATALENGTH(@ExpectedTenantRowVersion)<>8)
            THROW 50053,'Invalid tenant command.',1;
        DECLARE @Now datetimeoffset=SYSUTCDATETIME();
        """;

    internal static void Up(MigrationBuilder migration, string migrationId)
    {
        foreach (var table in new[] { "TenantEntries", "TenantOverrides" })
        {
            migration.Sql($"""
                CREATE SECURITY POLICY Gemology.{table}TenantPolicy
                ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Gemology.{table},
                ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Gemology.{table} AFTER INSERT,
                ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Gemology.{table} AFTER UPDATE WITH(STATE=ON);
                GRANT SELECT ON Gemology.{table} TO workbench_web;
                """);
            foreach (var role in new[] { "workbench_web", "workbench_worker", "workbench_operator" })
                migration.Sql($"DENY INSERT,UPDATE,DELETE ON Gemology.{table} TO {role};");
        }
        Procedure(migration, "SaveTenantEntry", """
            @ActorId uniqueidentifier,@EntryId uniqueidentifier,@ContentJson nvarchar(max),@ExpectedTenantRowVersion varbinary(max)=NULL
            """, ContentGuard + FieldAndSourceGuard + """
            IF EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=@EntryId)
                AND NOT EXISTS(SELECT 1 FROM Gemology.TenantEntries WHERE TenantId=@TenantId AND Id=@EntryId)
                THROW 50056,'A shared entry already uses this ID.',1;
            IF EXISTS(SELECT 1 FROM Gemology.TenantEntries WHERE TenantId=@TenantId AND Id=@EntryId)
            BEGIN
                UPDATE Gemology.TenantEntries SET ContentJson=@ContentJson,UpdatedBy=@ActorId,UpdatedAtUtc=@Now
                    WHERE TenantId=@TenantId AND Id=@EntryId AND RowVersion=@ExpectedTenantRowVersion;
                IF @@ROWCOUNT<>1 THROW 50054,'Tenant entry changed. Reload before saving.',1;
            END
            ELSE
            BEGIN
                IF @ExpectedTenantRowVersion IS NOT NULL THROW 50055,'Tenant entry was not found.',1;
                INSERT Gemology.TenantEntries(TenantId,Id,ContentJson,IsArchived,CreatedBy,UpdatedBy,CreatedAtUtc,UpdatedAtUtc)
                    VALUES(@TenantId,@EntryId,@ContentJson,0,@ActorId,@ActorId,@Now,@Now);
            END;
            SELECT * FROM Gemology.TenantEntries WHERE TenantId=@TenantId AND Id=@EntryId;
            """);
        Procedure(migration, "SetTenantEntryArchive", """
            @ActorId uniqueidentifier,@EntryId uniqueidentifier,@IsArchived bit,@ExpectedTenantRowVersion varbinary(max)
            """, """
            IF @IsArchived IS NULL OR @ExpectedTenantRowVersion IS NULL THROW 50053,'Invalid archive command.',1;
            IF NOT EXISTS(SELECT 1 FROM Gemology.TenantEntries WHERE TenantId=@TenantId AND Id=@EntryId)
                THROW 50055,'Tenant entry was not found.',1;
            UPDATE Gemology.TenantEntries SET IsArchived=@IsArchived,UpdatedBy=@ActorId,UpdatedAtUtc=@Now
                WHERE TenantId=@TenantId AND Id=@EntryId AND RowVersion=@ExpectedTenantRowVersion;
            IF @@ROWCOUNT<>1 THROW 50054,'Tenant entry changed. Reload before saving.',1;
            SELECT * FROM Gemology.TenantEntries WHERE TenantId=@TenantId AND Id=@EntryId;
            """);
        Procedure(migration, "SaveTenantOverrides", """
            @ActorId uniqueidentifier,@EntryId uniqueidentifier,@OverridesJson nvarchar(max),
            @ExpectedSharedRowVersion varbinary(max),@ExpectedTenantRowVersion varbinary(max)=NULL
            """, OverrideGuard + FieldAndSourceGuard + """
            IF @ExpectedSharedRowVersion IS NULL OR DATALENGTH(@ExpectedSharedRowVersion)<>8
                THROW 50053,'A shared version is required.',1;
            IF NOT EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=@EntryId) THROW 50055,'Shared entry was not found.',1;
            IF NOT EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=@EntryId AND RowVersion=@ExpectedSharedRowVersion)
                THROW 50054,'Shared entry changed. Reload before saving.',1;
            IF EXISTS(SELECT 1 FROM Gemology.TenantOverrides WHERE TenantId=@TenantId AND EntryId=@EntryId)
            BEGIN
                -- Preserve empty reset rows: their advancing version prevents reset/recreate ABA.
                UPDATE Gemology.TenantOverrides SET OverridesJson=@OverridesJson,UpdatedBy=@ActorId,UpdatedAtUtc=@Now
                    WHERE TenantId=@TenantId AND EntryId=@EntryId AND RowVersion=@ExpectedTenantRowVersion;
                IF @@ROWCOUNT<>1 THROW 50054,'Tenant overrides changed. Reload before saving.',1;
            END
            ELSE
            BEGIN
                IF @ExpectedTenantRowVersion IS NOT NULL THROW 50054,'Tenant overrides no longer exist.',1;
                INSERT Gemology.TenantOverrides(TenantId,EntryId,OverridesJson,CreatedBy,UpdatedBy,CreatedAtUtc,UpdatedAtUtc)
                    VALUES(@TenantId,@EntryId,@OverridesJson,@ActorId,@ActorId,@Now,@Now);
            END;
            SELECT * FROM Gemology.TenantOverrides WHERE TenantId=@TenantId AND EntryId=@EntryId;
            """);
        migration.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20261002192901_AddGemReferenceCuration',@Definition)=0
                THROW 50020,'Unsupported tenant gem reference readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20261002192901_AddGemReferenceCuration',N'{migrationId}');
            EXEC sys.sp_executesql @Definition;
            """);
    }

    private static void Procedure(MigrationBuilder migration, string name, string parameters, string body)
    {
        // The service acquires both locks before reading/revalidating the effective catalog; SQL never commits its transaction.
        migration.Sql($"CREATE PROCEDURE Gemology.{name} {parameters} AS BEGIN SET NOCOUNT ON; {AuthorityAndLocks} {body} END;");
        migration.Sql($"GRANT EXECUTE ON Gemology.{name} TO workbench_web;");
        migration.Sql($"DENY EXECUTE ON Gemology.{name} TO workbench_worker,workbench_operator;");
    }

    private const string ContentGuard = """
        IF @ContentJson IS NULL OR ISJSON(@ContentJson,OBJECT)<>1 OR DATALENGTH(@ContentJson)>2097152
            THROW 50053,'Invalid tenant content.',1;
        IF EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) GROUP BY [key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN
                (N'id',N'materialKind',N'commonName',N'aliases',N'group',N'species',N'variety',N'description',N'sources',N'notableLocality',N'isRetired',N'retirementExplanation',N'redirectEntryId'))
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'id' AND [type]=1 AND DATALENGTH([value])=72 AND TRY_CONVERT(uniqueidentifier,[value])=@EntryId)
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'materialKind' AND [type]=1)
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'commonName' AND [type]=1)
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'aliases' AND [type]=4)
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'sources' AND [type]=4)
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key]=N'isRetired' AND [type]=3 AND [value]=N'false')
            OR EXISTS(SELECT 1 FROM OPENJSON(@ContentJson) WHERE [key] IN(N'retirementExplanation',N'redirectEntryId') AND [type]<>0)
            THROW 50053,'Invalid tenant content structure or retirement metadata.',1;
        DECLARE @Fields TABLE(Name nvarchar(100) COLLATE Latin1_General_100_BIN2,Value nvarchar(max),Type int);
        INSERT @Fields SELECT [key],[value],[type] FROM OPENJSON(@ContentJson) WHERE [key] COLLATE Latin1_General_100_BIN2 IN
            (N'materialKind',N'commonName',N'aliases',N'group',N'species',N'variety',N'description',N'notableLocality');
        DECLARE @Sources TABLE(Content nvarchar(max),Field nvarchar(100) COLLATE Latin1_General_100_BIN2);
        IF EXISTS(SELECT 1 FROM OPENJSON(@ContentJson,'$.sources') WHERE [type]<>5) THROW 50053,'Invalid sources.',1;
        INSERT @Sources SELECT [value],JSON_VALUE([value],'$.field') FROM OPENJSON(@ContentJson,'$.sources');
        """;

    private const string OverrideGuard = """
        IF @OverridesJson IS NULL OR ISJSON(@OverridesJson,OBJECT)<>1 OR DATALENGTH(@OverridesJson)>2097152
            THROW 50053,'Invalid tenant overrides.',1;
        IF EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) GROUP BY [key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) WHERE [type]<>5 OR DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN
                (N'materialKind',N'commonName',N'aliases',N'group',N'species',N'variety',N'description',N'notableLocality'))
            THROW 50053,'Invalid override fields.',1;
        IF EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) f CROSS APPLY OPENJSON(f.[value]) p
            WHERE DATALENGTH(p.[key])<>DATALENGTH(RTRIM(p.[key])) OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN(N'state',N'value',N'sources'))
            OR EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) f CROSS APPLY OPENJSON(f.[value]) p
                GROUP BY f.[key],p.[key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) f WHERE
                NOT EXISTS(SELECT 1 FROM OPENJSON(f.[value]) WHERE [key]=N'state' AND [type]=1 AND DATALENGTH([value])=DATALENGTH(RTRIM([value])) AND [value] COLLATE Latin1_General_100_BIN2 IN(N'replace',N'clear'))
                OR NOT EXISTS(SELECT 1 FROM OPENJSON(f.[value]) WHERE [key]=N'sources' AND [type]=4)
                OR (JSON_VALUE(f.[value],'$.state')=N'replace' AND NOT EXISTS(SELECT 1 FROM OPENJSON(f.[value]) WHERE [key]=N'value' AND [type]<>0))
                OR (JSON_VALUE(f.[value],'$.state')=N'clear' AND
                    (f.[key] COLLATE Latin1_General_100_BIN2 NOT IN(N'group',N'species',N'variety',N'description',N'notableLocality')
                    OR EXISTS(SELECT 1 FROM OPENJSON(f.[value]) WHERE [key]=N'value' AND [type]<>0)
                    OR EXISTS(SELECT 1 FROM OPENJSON(f.[value],'$.sources')))))
            THROW 50053,'Invalid override choices.',1;
        DECLARE @Fields TABLE(Name nvarchar(100) COLLATE Latin1_General_100_BIN2,Value nvarchar(max),Type int);
        INSERT @Fields SELECT f.[key],p.[value],p.[type] FROM OPENJSON(@OverridesJson) f CROSS APPLY OPENJSON(f.[value]) p WHERE p.[key]=N'value';
        DECLARE @Sources TABLE(Content nvarchar(max),Field nvarchar(100) COLLATE Latin1_General_100_BIN2);
        IF EXISTS(SELECT 1 FROM OPENJSON(@OverridesJson) f CROSS APPLY OPENJSON(f.[value],'$.sources') s
            WHERE s.[type]<>5) THROW 50053,'Invalid sources.',1;
        INSERT @Sources SELECT s.[value],f.[key] FROM OPENJSON(@OverridesJson) f CROSS APPLY OPENJSON(f.[value],'$.sources') s;
        """;

    // SQL equality pads trailing spaces even under BIN2. Structural keys and enum tokens therefore also require exact length.
    // Effective classification, ordinary field-text normalization and FormKC duplicate identity remain the service's responsibility.
    private const string FieldAndSourceGuard = """
        IF EXISTS(SELECT 1 FROM @Fields WHERE
            (Name=N'materialKind' AND (Type<>1 OR DATALENGTH(Value)<>DATALENGTH(RTRIM(Value)) OR Value COLLATE Latin1_General_100_BIN2 NOT IN(N'mineral',N'mineraloid',N'organic',N'rockAggregate')))
            OR (Name=N'commonName' AND (Type<>1 OR LEN(LTRIM(RTRIM(Value)))=0 OR DATALENGTH(Value)>400))
            OR (Name IN(N'group',N'species',N'variety',N'description') AND Type<>0 AND
                (Type<>1 OR LEN(LTRIM(RTRIM(Value)))=0 OR DATALENGTH(Value)>CASE WHEN Name=N'description' THEN 4000 ELSE 400 END))
            OR (Name=N'aliases' AND Type<>4) OR (Name=N'notableLocality' AND Type NOT IN(0,5)))
            THROW 50053,'Invalid field values.',1;
        IF EXISTS(SELECT 1 FROM @Fields f WHERE Name=N'aliases' AND (SELECT COUNT(*) FROM OPENJSON(f.Value))>20)
            OR EXISTS(SELECT 1 FROM @Fields f CROSS APPLY OPENJSON(CASE WHEN f.Name=N'aliases' THEN f.Value ELSE N'[]' END) a
                WHERE a.[type]<>1 OR LEN(LTRIM(RTRIM(a.[value])))=0 OR DATALENGTH(a.[value])>400)
            THROW 50053,'Invalid aliases.',1;
        IF (SELECT COUNT(*) FROM @Sources)>64
            OR EXISTS(SELECT JSON_VALUE(Content,'$.id') FROM @Sources GROUP BY JSON_VALUE(Content,'$.id') HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM @Sources s WHERE
                TRY_CONVERT(uniqueidentifier,JSON_VALUE(Content,'$.id')) IS NULL
                OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(Content,'$.id'))='00000000-0000-0000-0000-000000000000'
                OR JSON_VALUE(Content,'$.field') IS NULL OR JSON_VALUE(Content,'$.field') COLLATE Latin1_General_100_BIN2<>s.Field
                OR NOT EXISTS(SELECT 1 FROM @Fields WHERE Name=s.Field AND Type<>0)
                OR COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Content,'$.title')))),0)=0 OR DATALENGTH(JSON_VALUE(Content,'$.title'))>400
                OR COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Content,'$.publisher')))),0)=0 OR DATALENGTH(JSON_VALUE(Content,'$.publisher'))>400
                OR (COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Content,'$.url')))),0)=0 AND COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Content,'$.citation')))),0)=0)
                OR DATALENGTH(JSON_VALUE(Content,'$.url'))>4000 OR DATALENGTH(JSON_VALUE(Content,'$.citation'))>4000
                OR TRY_CONVERT(date,JSON_VALUE(Content,'$.reviewedOn')) IS NULL OR TRY_CONVERT(date,JSON_VALUE(Content,'$.reviewedOn'))<='0001-01-01'
                OR (JSON_VALUE(Content,'$.accessedOn') IS NOT NULL AND
                    (TRY_CONVERT(date,JSON_VALUE(Content,'$.accessedOn')) IS NULL OR TRY_CONVERT(date,JSON_VALUE(Content,'$.accessedOn'))<='0001-01-01')))
            THROW 50053,'Invalid source assertions.',1;
        IF EXISTS(SELECT 1 FROM @Sources s CROSS APPLY OPENJSON(s.Content) p
            WHERE DATALENGTH(p.[key])<>DATALENGTH(RTRIM(p.[key])) OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN(N'id',N'field',N'title',N'publisher',N'url',N'citation',N'accessedOn',N'reviewedOn')
                OR (p.[key] IN(N'id',N'field',N'title',N'publisher',N'reviewedOn') AND p.[type]<>1)
                OR (p.[key] IN(N'url',N'citation',N'accessedOn') AND p.[type] NOT IN(0,1))
                OR (p.[key]=N'id' AND DATALENGTH(p.[value])<>72)
                OR (p.[key]=N'field' AND DATALENGTH(p.[value])<>DATALENGTH(RTRIM(p.[value])))
                OR (p.[key] IN(N'url',N'citation') AND p.[type]=1 AND (DATALENGTH(p.[value])>4000 OR LEN(LTRIM(RTRIM(p.[value])))=0))
                OR (p.[key]=N'accessedOn' AND p.[type]=1 AND (TRY_CONVERT(date,p.[value]) IS NULL OR TRY_CONVERT(date,p.[value])<='0001-01-01')))
            OR EXISTS(SELECT 1 FROM @Sources s CROSS APPLY OPENJSON(s.Content) p
                GROUP BY s.Content,p.[key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            THROW 50053,'Invalid source structure.',1;
        IF EXISTS(SELECT 1 FROM @Fields f WHERE Name=N'notableLocality' AND Type=5 AND
            (COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Value,'$.place')))),0)=0 OR DATALENGTH(JSON_VALUE(Value,'$.place'))>400
                OR COALESCE(LEN(LTRIM(RTRIM(JSON_VALUE(Value,'$.scope')))),0)=0 OR DATALENGTH(JSON_VALUE(Value,'$.scope'))>400
                OR TRY_CONVERT(date,JSON_VALUE(Value,'$.reviewedOn')) IS NULL
                OR NOT EXISTS(SELECT 1 FROM @Sources s WHERE s.Field=N'notableLocality'
                    AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.Content,'$.id'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(f.Value,'$.sourceAssertionId'))
                    AND TRY_CONVERT(date,JSON_VALUE(s.Content,'$.reviewedOn'))=TRY_CONVERT(date,JSON_VALUE(f.Value,'$.reviewedOn')))))
            THROW 50053,'Locality needs an identifiable matching source.',1;
        IF EXISTS(SELECT 1 FROM @Fields f CROSS APPLY OPENJSON(CASE WHEN Name=N'notableLocality' AND Type=5 THEN Value ELSE N'{}' END) p
            WHERE DATALENGTH(p.[key])<>DATALENGTH(RTRIM(p.[key])) OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN(N'place',N'scope',N'reviewedOn',N'sourceAssertionId') OR p.[type]<>1
                OR (p.[key]=N'sourceAssertionId' AND DATALENGTH(p.[value])<>72))
            OR EXISTS(SELECT 1 FROM @Fields f CROSS APPLY OPENJSON(CASE WHEN Name=N'notableLocality' AND Type=5 THEN Value ELSE N'{}' END) p
                GROUP BY f.Name,p.[key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            THROW 50053,'Invalid locality structure.',1;
        """;
}
