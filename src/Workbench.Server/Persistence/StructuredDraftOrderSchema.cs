// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static partial class StructuredDraftOrderSchema
{
    internal static void Protect(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql(SaveDraftV3);
        foreach (var operation in new[] { "Create", "Update" })
        {
            migrationBuilder.Sql($"""
                CREATE PROCEDURE [Purchasing].[{operation}DraftOrderV3]
                  @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                BEGIN
                  SET NOCOUNT ON;
                  EXEC [Purchasing].[SaveDraftOrderV3] @RequestId,@CanonicalInputJson,@ActorUserId,'{operation}';
                END;
                """);
            migrationBuilder.Sql($"GRANT EXECUTE ON [Purchasing].[{operation}DraftOrderV3] TO [workbench_web];");
        }
        migrationBuilder.Sql("""
            DECLARE @Old nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrderV2]'));
            SET @Old=REPLACE(@Old,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Old=REPLACE(@Old,N'DECLARE @Fields TABLE',N'THROW 50426,''Reload this draft with the current contract.'',1; DECLARE @Fields TABLE');
            SET @Old=REPLACE(@Old,N'AND Id=@TargetId AND IsDeleted=0)',N'AND Id=@TargetId)');
            EXEC sys.sp_executesql @Old;
            SET @Old=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrder]'));
            SET @Old=REPLACE(@Old,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Old=REPLACE(@Old,N'AND Id=@TargetId AND IsDeleted=0)',N'AND Id=@TargetId)');
            EXEC sys.sp_executesql @Old;
            """);
        migrationBuilder.Sql($"""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260912064156_AddSupplierIdentityAndPurchaseReferences',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
