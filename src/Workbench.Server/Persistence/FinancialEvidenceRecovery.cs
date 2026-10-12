// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceRecovery
{
    internal static void Create(MigrationBuilder migration)
    {
        // Logical disposal is durable authority; elapsed grace controls physical deletion independently.
        migration.Sql("""
            CREATE FUNCTION Storage.FinanciallyProtectedRevision(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              RETURN CASE WHEN EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks
                WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId) THEN 1 ELSE 0 END;
            END;
            """);

    }
}
