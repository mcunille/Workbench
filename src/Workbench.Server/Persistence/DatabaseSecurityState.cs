// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Persistence;

public sealed record DatabaseSecurityState(
    bool CompatibleMigration,
    bool TenantPolicyEnabled,
    bool TenantPolicyAlterDenied,
    bool KeyTableAvailable,
    long RestoreGeneration,
    long RestoreSanitizedGeneration)
{
    public bool IsReady =>
        CompatibleMigration &&
        TenantPolicyEnabled &&
        TenantPolicyAlterDenied &&
        KeyTableAvailable &&
        RestoreGeneration == RestoreSanitizedGeneration;
}
