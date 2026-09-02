// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Workbench.Server.Tenancy;

public sealed class TenantConnectionInterceptor(TenantContext tenantContext) : DbConnectionInterceptor
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_set_session_context
                @key = N'TenantId',
                @value = @tenantId,
                @read_only = 1;
            """;
        command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier)
        {
            Value = tenantContext.TenantId is Guid tenantId ? tenantId : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
