// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class GemReferencePublishSchema
{
    internal static void Up(MigrationBuilder migration)
    {
        GemReferenceCurationSchema.Procedure(migration, "PublishDraftBatch", """
            @AccountId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
            @SelectionJson nvarchar(max),@EntriesJson nvarchar(max),@OutcomeJson nvarchar(max),@SummaryJson nvarchar(max),@AuditOnly bit=0
            """, """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @Lock int;
            EXEC @Lock=sys.sp_getapplock @Resource=N'Gemology.Publication',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Lock<0 THROW 50042,'Catalog is busy. Retry the request.',1;
            IF NOT EXISTS(SELECT 1 FROM ServiceAdministration.Sessions s WITH(HOLDLOCK) JOIN ServiceAdministration.Accounts a WITH(HOLDLOCK) ON a.Id=s.AccountId
                WHERE s.Id=@SessionId AND a.Id=@AccountId AND a.IsEnabled=1 AND s.SecurityVersion=a.SecurityVersion
                    AND s.RevokedAtUtc IS NULL AND s.IdleExpiresAtUtc>SYSUTCDATETIME() AND s.AbsoluteExpiresAtUtc>SYSUTCDATETIME())
                THROW 50041,'Current service-admin authority is required.',1;
            IF @RequestId='00000000-0000-0000-0000-000000000000' OR ISJSON(@SelectionJson)<>1 OR ISJSON(@EntriesJson)<>1
                OR ISJSON(@OutcomeJson)<>1 OR ISJSON(@SummaryJson)<>1 OR DATALENGTH(@EntriesJson)>104857600
                OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@OutcomeJson,'$.requestId'))<>@RequestId
                THROW 50043,'Invalid publication request.',1;
            DECLARE @Code nvarchar(40)=JSON_VALUE(@OutcomeJson,'$.code');
            IF @Code IS NULL OR @Code NOT IN ('published','validation_failed','stale_entry','request_conflict','infrastructure_failure')
                THROW 50043,'Invalid publication outcome.',1;
            IF @AuditOnly=1
            BEGIN
                IF @Code<>'infrastructure_failure' THROW 50043,'Invalid failure audit.',1;
                INSERT Gemology.PublicationAudit VALUES(NEWID(),@RequestId,@AccountId,@Code,@SummaryJson,@Now);
                COMMIT;
                RETURN;
            END;
            IF EXISTS(SELECT 1 FROM Gemology.PublishRequests WHERE Id=@RequestId)
            BEGIN
                IF EXISTS(SELECT 1 FROM Gemology.PublishRequests WHERE Id=@RequestId AND AccountId=@AccountId
                    AND SelectionJson COLLATE Latin1_General_100_BIN2=@SelectionJson COLLATE Latin1_General_100_BIN2)
                    SELECT OutcomeJson FROM Gemology.PublishRequests WHERE Id=@RequestId;
                ELSE
                BEGIN
                    SET @OutcomeJson=JSON_MODIFY(JSON_MODIFY(@OutcomeJson,'$.code','request_conflict'),'$.entries',JSON_QUERY('[]'));
                    INSERT Gemology.PublicationAudit VALUES(NEWID(),@RequestId,@AccountId,'request_conflict',@SummaryJson,@Now);
                    SELECT @OutcomeJson;
                END;
                COMMIT;
                RETURN;
            END;
            CREATE TABLE #Selection(Id uniqueidentifier PRIMARY KEY,Version nvarchar(16) NOT NULL);
            INSERT #Selection SELECT draftId,expectedDraftRowVersion FROM OPENJSON(@SelectionJson) WITH(draftId uniqueidentifier,expectedDraftRowVersion nvarchar(16));
            IF (SELECT COUNT(*) FROM #Selection) NOT BETWEEN 1 AND 50 THROW 50043,'Select one to fifty distinct drafts.',1;
            IF @Code='published'
            BEGIN
                CREATE TABLE #Batch(DraftId uniqueidentifier PRIMARY KEY,Id uniqueidentifier UNIQUE NOT NULL,ContentJson nvarchar(max) NOT NULL,
                    IdentityKey binary(32) NOT NULL,AliasesJson nvarchar(max) NOT NULL,
                    MaterialKind nvarchar(max),CommonName nvarchar(max),[Group] nvarchar(max),Species nvarchar(max),Variety nvarchar(max),[Description] nvarchar(max),
                    IsRetired bit,RetirementExplanation nvarchar(max),RedirectEntryId uniqueidentifier);
                INSERT #Batch SELECT p.draftId,p.entryId,p.content,CONVERT(binary(32),p.identityKey,2),p.aliases,
                    c.materialKind,c.commonName,c.[group],c.species,c.variety,c.description,c.isRetired,c.retirementExplanation,c.redirectEntryId
                    FROM OPENJSON(@EntriesJson) WITH(draftId uniqueidentifier,entryId uniqueidentifier,content nvarchar(max) AS JSON,identityKey varchar(64),aliases nvarchar(max) AS JSON) p
                    CROSS APPLY OPENJSON(p.content) WITH(materialKind nvarchar(max),commonName nvarchar(max),[group] nvarchar(max),species nvarchar(max),
                        variety nvarchar(max),description nvarchar(max),isRetired bit,retirementExplanation nvarchar(max),redirectEntryId uniqueidentifier) c;
                IF (SELECT COUNT(*) FROM #Batch)<>(SELECT COUNT(*) FROM #Selection)
                    OR EXISTS(SELECT 1 FROM #Batch b LEFT JOIN #Selection s ON s.Id=b.DraftId LEFT JOIN Gemology.Drafts d ON d.Id=b.DraftId
                        WHERE s.Id IS NULL OR d.Id IS NULL OR d.EntryId<>b.Id OR b.ContentJson COLLATE Latin1_General_100_BIN2<>d.ContentJson COLLATE Latin1_General_100_BIN2
                            OR JSON_VALUE((SELECT d.RowVersion AS version FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),'$.version')<>s.Version
                            OR (d.ExpectedPublishedRowVersion IS NULL AND EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=b.Id))
                            OR (d.ExpectedPublishedRowVersion IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=b.Id AND RowVersion=d.ExpectedPublishedRowVersion)))
                    THROW 50044,'Selected draft or published entry changed.',1;
                IF EXISTS(SELECT 1 FROM #Batch WHERE MaterialKind IS NULL OR MaterialKind COLLATE Latin1_General_100_BIN2 NOT IN ('mineral','mineraloid','organic','rockAggregate')
                    OR CommonName IS NULL OR LEN(LTRIM(RTRIM(CommonName)))=0 OR DATALENGTH(CommonName)>400
                    OR (MaterialKind='mineral' AND (Species IS NULL OR LEN(LTRIM(RTRIM(Species)))=0))
                    OR DATALENGTH(Species)>400 OR DATALENGTH([Group])>400 OR DATALENGTH(Variety)>400 OR DATALENGTH([Description])>4000
                    OR IsRetired IS NULL OR (IsRetired=0 AND (RetirementExplanation IS NOT NULL OR RedirectEntryId IS NOT NULL))
                    OR (IsRetired=1 AND RedirectEntryId IS NULL AND NULLIF(LTRIM(RTRIM(RetirementExplanation)),'') IS NULL))
                    THROW 50043,'Invalid shared classification.',1;
                IF EXISTS(SELECT IdentityKey FROM #Batch WHERE IsRetired=0 GROUP BY IdentityKey HAVING COUNT(*)>1)
                    OR EXISTS(SELECT 1 FROM #Batch b JOIN Gemology.Entries e ON b.IdentityKey=e.IdentityKey
                        WHERE b.IsRetired=0 AND e.IsRetired=0 AND NOT EXISTS(SELECT 1 FROM #Batch WHERE Id=e.Id))
                    THROW 50043,'Duplicate shared identity.',1;
                CREATE TABLE #Sources(Id uniqueidentifier PRIMARY KEY,EntryId uniqueidentifier NOT NULL,Field nvarchar(max),Title nvarchar(max),Publisher nvarchar(max),
                    Url nvarchar(max),Citation nvarchar(max),AccessedOn date,ReviewedOn date);
                INSERT #Sources SELECT s.id,b.Id,s.field,s.title,s.publisher,s.url,s.citation,s.accessedOn,s.reviewedOn
                    FROM #Batch b CROSS APPLY OPENJSON(b.ContentJson,'$.sources') WITH(id uniqueidentifier,field nvarchar(max),title nvarchar(max),publisher nvarchar(max),
                        url nvarchar(max),citation nvarchar(max),accessedOn date,reviewedOn date) s;
                CREATE TABLE #Fields(EntryId uniqueidentifier,Field nvarchar(30));
                INSERT #Fields SELECT b.Id,f.Field FROM #Batch b CROSS APPLY(VALUES
                    ('materialKind',1),('commonName',1),('group',CASE WHEN b.[Group] IS NULL THEN 0 ELSE 1 END),
                    ('species',CASE WHEN b.Species IS NULL THEN 0 ELSE 1 END),('variety',CASE WHEN b.Variety IS NULL THEN 0 ELSE 1 END),
                    ('description',CASE WHEN b.[Description] IS NULL THEN 0 ELSE 1 END),
                    ('aliases',CASE WHEN EXISTS(SELECT 1 FROM OPENJSON(b.ContentJson,'$.aliases')) THEN 1 ELSE 0 END),
                    ('notableLocality',CASE WHEN JSON_QUERY(b.ContentJson,'$.notableLocality') IS NULL THEN 0 ELSE 1 END)) f(Field,Populated) WHERE f.Populated=1;
                IF EXISTS(SELECT 1 FROM #Fields f WHERE NOT EXISTS(SELECT 1 FROM #Sources s WHERE s.EntryId=f.EntryId AND s.Field COLLATE Latin1_General_100_BIN2=f.Field))
                    OR EXISTS(SELECT 1 FROM #Sources s WHERE s.Id='00000000-0000-0000-0000-000000000000'
                        OR NOT EXISTS(SELECT 1 FROM #Fields f WHERE f.EntryId=s.EntryId AND f.Field=s.Field COLLATE Latin1_General_100_BIN2)
                        OR NULLIF(LTRIM(RTRIM(s.Title)),'') IS NULL OR NULLIF(LTRIM(RTRIM(s.Publisher)),'') IS NULL
                        OR DATALENGTH(s.Title)>400 OR DATALENGTH(s.Publisher)>400 OR DATALENGTH(s.Url)>4000 OR DATALENGTH(s.Citation)>4000
                        OR (NULLIF(LTRIM(RTRIM(s.Url)),'') IS NULL AND NULLIF(LTRIM(RTRIM(s.Citation)),'') IS NULL)
                        OR (s.Url IS NOT NULL AND s.Url NOT LIKE 'https://_%' AND s.Url NOT LIKE 'http://_%')
                        OR s.ReviewedOn IS NULL OR s.ReviewedOn<='0001-01-01' OR s.ReviewedOn>CONVERT(date,@Now)
                        OR s.AccessedOn<='0001-01-01' OR s.AccessedOn>CONVERT(date,@Now))
                    OR EXISTS(SELECT EntryId FROM #Sources GROUP BY EntryId HAVING COUNT(*)>64)
                    OR EXISTS(SELECT 1 FROM #Sources s JOIN Gemology.SourceAssertions e ON e.Id=s.Id WHERE e.EntryId<>s.EntryId)
                    THROW 50043,'Invalid shared source assertions.',1;
                CREATE TABLE #Final(Id uniqueidentifier PRIMARY KEY,RedirectEntryId uniqueidentifier NULL);
                INSERT #Final SELECT Id,RedirectEntryId FROM Gemology.Entries WHERE Id NOT IN(SELECT Id FROM #Batch)
                    UNION ALL SELECT Id,RedirectEntryId FROM #Batch;
                IF EXISTS(SELECT 1 FROM #Final f WHERE f.RedirectEntryId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM #Final WHERE Id=f.RedirectEntryId))
                    THROW 50043,'Unknown retirement redirect.',1;
                DECLARE @Cycle bit=0;
                WITH Walk AS(
                    SELECT Id AS Start,RedirectEntryId AS Target,CONVERT(nvarchar(max),'|'+CONVERT(nvarchar(36),Id)+'|') AS Path,CONVERT(bit,0) AS Cycle FROM #Final WHERE RedirectEntryId IS NOT NULL
                    UNION ALL SELECT w.Start,f.RedirectEntryId,w.Path+CONVERT(nvarchar(36),f.Id)+'|',CONVERT(bit,CASE WHEN CHARINDEX('|'+CONVERT(nvarchar(36),f.Id)+'|',w.Path)>0 THEN 1 ELSE 0 END)
                    FROM Walk w JOIN #Final f ON f.Id=w.Target WHERE w.Cycle=0)
                    SELECT @Cycle=1 FROM Walk WHERE Cycle=1 OPTION(MAXRECURSION 0);
                IF @Cycle=1 THROW 50043,'Retirement redirect cycle.',1;
                -- Temporarily retire selected existing rows to permit an identity swap/replacement in any selection order.
                UPDATE e SET IsRetired=1,RetirementExplanation=N'Publication in progress',RedirectEntryId=NULL FROM Gemology.Entries e JOIN #Batch b ON b.Id=e.Id;
                INSERT Gemology.Entries(Id,MaterialKind,CommonName,[Group],Species,Variety,[Description],IdentityKey,IsRetired,RetirementExplanation,RedirectEntryId)
                    SELECT b.Id,b.MaterialKind,b.CommonName,b.[Group],b.Species,b.Variety,b.[Description],b.IdentityKey,1,N'Publication in progress',NULL FROM #Batch b
                    WHERE NOT EXISTS(SELECT 1 FROM Gemology.Entries WHERE Id=b.Id);
                DELETE l FROM Gemology.LocalityAssertions l JOIN #Batch b ON b.Id=l.EntryId;
                DELETE a FROM Gemology.Aliases a JOIN #Batch b ON b.Id=a.EntryId;
                DELETE s FROM Gemology.SourceAssertions s JOIN #Batch b ON b.Id=s.EntryId;
                UPDATE e SET MaterialKind=b.MaterialKind,CommonName=b.CommonName,[Group]=b.[Group],Species=b.Species,Variety=b.Variety,[Description]=b.[Description],
                    IdentityKey=b.IdentityKey,IsRetired=b.IsRetired,RetirementExplanation=b.RetirementExplanation,RedirectEntryId=b.RedirectEntryId
                    FROM Gemology.Entries e JOIN #Batch b ON b.Id=e.Id;
                INSERT Gemology.SourceAssertions(Id,EntryId,Field,Title,Publisher,Url,Citation,AccessedOn,ReviewedOn)
                    SELECT Id,EntryId,Field,Title,Publisher,Url,Citation,AccessedOn,ReviewedOn FROM #Sources;
                INSERT Gemology.Aliases(EntryId,Position,Name,NormalizedName)
                    SELECT b.Id,a.position,a.name,a.normalizedName FROM #Batch b CROSS APPLY OPENJSON(b.AliasesJson)
                    WITH(position int,name nvarchar(200),normalizedName nvarchar(4000)) a;
                INSERT Gemology.LocalityAssertions(EntryId,Place,Scope,ReviewedOn,SourceAssertionId,SourceField)
                    SELECT b.Id,l.place,l.scope,l.reviewedOn,l.sourceAssertionId,'notableLocality' FROM #Batch b
                    CROSS APPLY OPENJSON(b.ContentJson,'$.notableLocality') WITH(place nvarchar(200),scope nvarchar(200),reviewedOn date,sourceAssertionId uniqueidentifier) l;
                SET @OutcomeJson=JSON_MODIFY(@OutcomeJson,'$.entries',JSON_QUERY((SELECT e.Id AS entryId,e.RowVersion AS rowVersion FROM Gemology.Entries e JOIN #Batch b ON b.Id=e.Id ORDER BY e.Id FOR JSON PATH)));
                DELETE d FROM Gemology.Drafts d JOIN #Batch b ON b.DraftId=d.Id;
            END;
            INSERT Gemology.PublishRequests VALUES(@RequestId,@AccountId,@SelectionJson,@OutcomeJson,@Now);
            INSERT Gemology.PublicationAudit VALUES(NEWID(),@RequestId,@AccountId,@Code,@SummaryJson,@Now);
            SELECT @OutcomeJson;
            COMMIT;
            """);
    }
}
