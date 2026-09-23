// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Persistence;

// Explicit release contract, independently checked against EF's migration inventory in tests.
// Append ordinary migrations here; never derive this list from EF or edit historical SQL.
public static class CurrentSchema
{
    public static IReadOnlyList<string> Migrations { get; } = Array.AsReadOnly<string>(
    [
        "20260904061204_InitialSchema",
        "20260904061246_EstablishSecurityBoundaries",
        "20260905222755_AddBlobAndOperationalProviders",
        "20260906031109_AddDeploymentQueueTelemetry",
        "20260906092000_DeferInvitationIdentityClaim",
        "20260907054000_AddProviderRetryDelay",
        "20260907060000_AddCollectionNotebook",
        "20260907082353_AddItemPhotographs",
        "20260907194500_AddItemDetailEditing",
        "20260907224158_AddItemArchiving",
        "20260907225320_AddOnlineRecovery",
        "20260908010000_AddItemRestoration",
        "20260909034719_AddAcquisitionContext",
        "20260910071000_AddSharedAcquisitions",
        "20260911184933_AddAcquisitionDocuments",
        "20260912030844_AddDraftSupplierOrders",
        "20260912064156_AddSupplierIdentityAndPurchaseReferences",
        "20260917010000_AddSupplierBasedDraftPricing",
        "20260917015000_PrepareRetainedBetaFinancialUpgrade",
        "20260917020000_AddDraftFinancialAdjustments",
        "20260917030000_ProtectConfirmedSupplierChargeCorrections",
        "20260917080000_ConsolidateBetaDraftCommands",
        "20260918010000_RemoveHistoricalDraftReplay",
        "20260918020000_IntegrateBetaDraftFinancialAdjustments",
        "20260918060000_AddPurchaseOrderCommitment",
        "20260918061646_AddPurchaseOrderDocuments",
        "20260918063409_HardenPurchaseOrderDocumentAuthority",
        "20260921041331_MakeSupplierProfilesCustom",
        "20260921051843_AddAccountingFoundation",
        "20260923010000_AddAtomicJournal",
    ]);

    public static string MigrationId => Migrations[^1];
}

