// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations
{
    public partial class AddSupplierCreditsAndRefunds
    {
        private static void CreateSupplierSourceCorrectionIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_ClearingAccountId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "ClearingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_CorrectionId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "CorrectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_JournalId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_OpeningMovementId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "OpeningMovementId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlementReleases_TenantId_RestoredMovementId",
                schema: "Purchasing",
                table: "SupplierCashSettlementReleases",
                columns: new[] { "TenantId", "RestoredMovementId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrectionMembers_TenantId_CorrectionId_Kind_EntityId",
                schema: "Purchasing",
                table: "SupplierSourceCorrectionMembers",
                columns: new[] { "TenantId", "CorrectionId", "Kind", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrectionMembers_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierSourceCorrectionMembers",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrectionMembers_TenantId_ItemId",
                schema: "Purchasing",
                table: "SupplierSourceCorrectionMembers",
                columns: new[] { "TenantId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrectionMembers_TenantId_JournalId",
                schema: "Purchasing",
                table: "SupplierSourceCorrectionMembers",
                columns: new[] { "TenantId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrectionMembers_TenantId_RecognitionEventId",
                schema: "Purchasing",
                table: "SupplierSourceCorrectionMembers",
                columns: new[] { "TenantId", "RecognitionEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_OriginalBillId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "OriginalBillId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_OriginalCashSourceId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "OriginalCashSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_OriginalCreditId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "OriginalCreditId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_PurchaseOrderId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_SourceKind_SourceId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "SourceKind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceCorrections_TenantId_SupplierId",
                schema: "Purchasing",
                table: "SupplierSourceCorrections",
                columns: new[] { "TenantId", "SupplierId" });
        }
    }
}