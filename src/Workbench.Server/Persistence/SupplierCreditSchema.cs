// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class SupplierCreditSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        foreach (var procedure in new[]
        {
            "Purchasing.CanonicalizeSupplierCredit", "Purchasing.CanonicalizeSupplierCash", "Purchasing.SupplierCreditPosition",
            "Purchasing.AssertSupplierCreditCapacity", "Purchasing.ValidateSupplierCreditClaims", "Purchasing.AppendSupplierCredit",
            "Purchasing.SupplierCreditControl", "Purchasing.SupplierItemControl",
            "Purchasing.AppendSupplierCashSettlement", "Purchasing.SupplierCashControl",
            "Accounting.FinancialEvidenceSource", "Accounting.AppendFinancialEvidence",
            "Purchasing.RequireSupplierComposition", "Purchasing.ApplySupplierFundsCore",
            "Accounting.PostJournal", "Purchasing.PostRecognition", "Purchasing.PostSupplierCredit", "Purchasing.RecordSupplierRefund", "Security.ReadDatabaseReadiness"
        })
        {
            // Large predecessor definitions remain one SQL batch, stored in bounded authored parts.
            var parts = procedure is "Purchasing.PostRecognition" or "Accounting.PostJournal"
                ? new[] { $"{procedure}.1", $"{procedure}.2" } : new[] { procedure };
            var sql = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                using var stream = typeof(SupplierCreditSchema).Assembly
                    .GetManifestResourceStream($"Workbench.SupplierCredits.{part}.sql")
                    ?? throw new InvalidOperationException($"Missing supplier credit procedure: {part}.");
                using var reader = new StreamReader(stream);
                sql.Append(reader.ReadToEnd());
            }
            migrationBuilder.Sql(sql.ToString());
        }

        foreach (var table in new[] { "SupplierCredits", "SupplierCreditClaims", "SupplierCreditVersions", "SupplierCashSources", "SupplierCashSettlements", "SupplierCashVersions" })
        {
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                  ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table},
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER INSERT,
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER UPDATE;
                GRANT SELECT ON Purchasing.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Purchasing.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Purchasing.{table} TO workbench_worker;
                """);
        }
        foreach (var table in new[] { "SupplierCredits", "SupplierCreditClaims", "SupplierCashSources", "SupplierCashSettlements" })
        {
            migrationBuilder.Sql($"""
                CREATE TRIGGER Purchasing.Preserve{table} ON Purchasing.{table} AFTER UPDATE,DELETE AS BEGIN
                  SET NOCOUNT ON;
                  IF EXISTS(SELECT 1 FROM deleted) THROW 51009,'Supplier credit financial history is immutable.',1;
                END;
                """);
        }
        foreach (var procedure in new[] { "CanonicalizeSupplierCredit", "AppendSupplierCredit", "AssertSupplierCreditCapacity", "ValidateSupplierCreditClaims", "CanonicalizeSupplierCash", "AppendSupplierCashSettlement" })
        {
            migrationBuilder.Sql($"""
                DENY EXECUTE ON Purchasing.{procedure} TO workbench_web;
                DENY EXECUTE ON Purchasing.{procedure} TO workbench_worker;
                """);
        }
        migrationBuilder.Sql("GRANT SELECT ON Purchasing.SupplierCreditControl TO workbench_web;");
        migrationBuilder.Sql("GRANT SELECT ON Purchasing.SupplierCashControl TO workbench_web;");
        migrationBuilder.Sql($"""
            IF CHARINDEX(N'{migrationId}',OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness')))=0
              THROW 50020,'Supplier credit readiness marker is incomplete.',1;
            """);
    }
}
