// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Workbench.Server.Tenancy;

public sealed class TenantConnectionInterceptor(
    TenantContext tenantContext,
    TenantContextProof proof) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SetTenantContext(connection, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await SetTenantContext(connection, cancellationToken);

    private async Task SetTenantContext(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await proof.ApplyAsync(connection, tenantContext.TenantId, cancellationToken);
    }
}
