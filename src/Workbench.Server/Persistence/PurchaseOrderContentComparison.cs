// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class PurchaseOrderContentComparison
{
    // Called only after full content validation. Compare decoded members at each level:
    // object property order/escaping is incidental, while array positions and scalar values matter.
    // This deliberately does not change the exact-input fingerprint used by request receipts.
    internal const string Create = """
        CREATE PROCEDURE Purchasing.ComparePurchaseOrderContent
          @Left nvarchar(max),@Right nvarchar(max),@Equal bit OUTPUT
        AS
        BEGIN
          SET NOCOUNT ON;
          SET @Equal=1;
          DECLARE @Pending TABLE(Id int IDENTITY PRIMARY KEY,LeftJson nvarchar(max),RightJson nvarchar(max));
          DECLARE @LeftMembers TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
          DECLARE @RightMembers TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
          INSERT @Pending VALUES(@Left,@Right);
          DECLARE @Index int=1;
          WHILE EXISTS(SELECT 1 FROM @Pending WHERE Id=@Index)
          BEGIN
            SELECT @Left=LeftJson,@Right=RightJson FROM @Pending WHERE Id=@Index;
            DELETE FROM @LeftMembers; DELETE FROM @RightMembers;
            INSERT @LeftMembers SELECT [key],[value],[type] FROM OPENJSON(@Left);
            INSERT @RightMembers SELECT [key],[value],[type] FROM OPENJSON(@Right);
            IF EXISTS(
              SELECT 1 FROM @LeftMembers a FULL JOIN @RightMembers b
                ON CONVERT(varbinary(max),a.[key])=CONVERT(varbinary(max),b.[key])
              WHERE a.[key] IS NULL OR b.[key] IS NULL OR a.[type]<>b.[type]
                OR (a.[type] NOT IN(4,5) AND CONVERT(varbinary(max),COALESCE(a.[value],N''))<>CONVERT(varbinary(max),COALESCE(b.[value],N''))))
            BEGIN
              SET @Equal=0; RETURN;
            END;
            INSERT @Pending(LeftJson,RightJson)
              SELECT a.[value],b.[value] FROM @LeftMembers a JOIN @RightMembers b
                ON CONVERT(varbinary(max),a.[key])=CONVERT(varbinary(max),b.[key]) WHERE a.[type] IN(4,5);
            DELETE FROM @Pending WHERE Id=@Index;
            SET @Index+=1;
          END;
        END;
        """;
}
