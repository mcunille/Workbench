// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierAllocationAvailability
{
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.AssertSupplierAvailability
          @TenantId uniqueidentifier,@ProposedMovements nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 OR @TenantId IS NULL
            OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Supplier availability requires the tenant accounting lock.',1;
          IF @ProposedMovements IS NULL OR ISJSON(@ProposedMovements,ARRAY)<>1 OR DATALENGTH(@ProposedMovements)>1048576
            THROW 51000,'Invalid proposed supplier movements.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@ProposedMovements) e WHERE e.type<>5
              OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>4)
            OR EXISTS(SELECT 1 FROM OPENJSON(@ProposedMovements) e CROSS APPLY OPENJSON(e.value) p
              WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('itemId','groupId','postingDate','amount') OR p.type<>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@ProposedMovements) e CROSS APPLY OPENJSON(e.value) p
              GROUP BY e.[key],p.[key] HAVING COUNT(*)>1)
            THROW 51000,'Invalid proposed movement properties.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@ProposedMovements) e CROSS APPLY OPENJSON(e.value) p
            CROSS APPLY(SELECT CASE WHEN LEFT(p.value,1)='-' THEN SUBSTRING(p.value,2,100) ELSE p.value END magnitude) n
            WHERE (p.[key] IN('itemId','groupId') AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL
                OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
              OR (p.[key]='postingDate' AND (DATALENGTH(p.value)<>20 OR TRY_CONVERT(date,p.value,23) IS NULL))
              OR (p.[key]='amount' AND (LEN(n.magnitude)=0 OR n.magnitude COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
                OR LEFT(n.magnitude,1)='.' OR RIGHT(n.magnitude,1)='.' OR LEN(n.magnitude)-LEN(REPLACE(n.magnitude,'.',''))>1
                OR LEN(LEFT(n.magnitude,CHARINDEX('.',n.magnitude+'.')-1))>24
                OR LEN(SUBSTRING(n.magnitude,CHARINDEX('.',n.magnitude+'.')+1,100))>4
                OR TRY_CONVERT(decimal(28,4),p.value) IS NULL)))
            THROW 51000,'Invalid exact proposed movement value.',1;
          DECLARE @Proposed TABLE(ItemId uniqueidentifier,GroupId uniqueidentifier,PostingDate date,Amount decimal(28,4));
          INSERT @Proposed SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),
            CONVERT(uniqueidentifier,JSON_VALUE(value,'$.groupId')),CONVERT(date,JSON_VALUE(value,'$.postingDate'),23),
            CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@ProposedMovements);
          DECLARE @Balances TABLE(Amount decimal(38,4));
          -- Group first, then date: no intermediate row within an atomic date is a capacity boundary.
          WITH Movements AS(
            SELECT ItemId,GroupId,PostingDate,CONVERT(decimal(38,4),Amount) Amount FROM @Proposed
            UNION ALL
            SELECT m.ItemId,m.GroupId,m.PostingDate,CONVERT(decimal(38,4),m.Amount)
              FROM Purchasing.SupplierItemMovements m WITH(UPDLOCK,HOLDLOCK)
              WHERE m.TenantId=@TenantId AND EXISTS(SELECT 1 FROM @Proposed p WHERE p.ItemId=m.ItemId)
          ), Groups AS(
            SELECT ItemId,GroupId,PostingDate,SUM(Amount) Amount FROM Movements GROUP BY ItemId,GroupId,PostingDate
          ), Dates AS(
            SELECT ItemId,PostingDate,SUM(Amount) Amount FROM Groups GROUP BY ItemId,PostingDate
          )
          INSERT @Balances SELECT SUM(Amount) OVER(PARTITION BY ItemId ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) FROM Dates;
          IF EXISTS(SELECT 1 FROM @Balances WHERE Amount<0 OR TRY_CONVERT(decimal(28,4),Amount) IS NULL)
            THROW 51009,'Supplier capacity is unavailable at a posting boundary.',1;
        END;
        """;
}
