// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceProcedures
{
    internal static void Create(MigrationBuilder migration)
    {
        // Frozen final definitions for this release; merged migrations remain unchanged.
        foreach (var procedure in new[]
        {
            "Administration.SanitizeRestore", "Operations.CompleteWork", "Operations.LockWork",
            "Purchasing.CanonicalizeRecognition", "Purchasing.FinishPurchaseOrderDocument",
            "Purchasing.PlanSupplierCorrection", "Purchasing.PostRecognition", "Purchasing.PostSupplierBill",
            "Purchasing.PreparePurchaseOrderDocument", "Purchasing.ReadSupplierBill",
            "Purchasing.RecordSupplierPaymentCore", "Purchasing.ValidateBillEvidence",
            "Security.ReadDatabaseReadiness", "Storage.AcceptFileRecovery", "Storage.ExportManifest",
            "Storage.ReadRecoveryInventory", "Storage.RelocateRevision", "Storage.ReplayDeletion",
        })
        {
            using var stream = typeof(FinancialEvidenceProcedures).Assembly
                .GetManifestResourceStream($"Workbench.FinancialEvidence.{procedure}.sql")
                ?? throw new InvalidOperationException($"Missing financial evidence procedure: {procedure}.");
            using var reader = new StreamReader(stream);
            migration.Sql(reader.ReadToEnd());
        }
    }
}
