// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public sealed class DatabaseReadinessCheck(
    string connectionString,
    TenantContextProof tenantContextProof) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("[Security].[ReadDatabaseReadiness]", connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            DatabaseSecurityState? state;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return HealthCheckResult.Unhealthy("Database security state is missing.");
                }

                state = new DatabaseSecurityState(
                    reader.GetBoolean(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8),
                    ApplicationTenantProofAccepted: false);
            }

            var sentinelTenant = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            await tenantContextProof.ApplyAsync(connection, sentinelTenant, cancellationToken);
            await using var proofCommand = new SqlCommand(
                "SELECT COUNT(*) FROM [Security].[fn_tenant_access](@tenantId)",
                connection);
            proofCommand.Parameters.AddWithValue("@tenantId", sentinelTenant);
            state = state with
            {
                ApplicationTenantProofAccepted = Convert.ToInt32(
                    await proofCommand.ExecuteScalarAsync(cancellationToken)) == 1,
            };
            await using var operational = new SqlCommand("[Security].[ReadOperationalReadiness]", connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            var operationalReady = Convert.ToBoolean(await operational.ExecuteScalarAsync(cancellationToken));
            await using var deployment = new SqlCommand("[Security].[ReadDeploymentReadiness]", connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            var deploymentReady = Convert.ToBoolean(await deployment.ExecuteScalarAsync(cancellationToken));
            return state.IsReady && operationalReady && deploymentReady
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database security state is not ready.");
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Database readiness check failed.");
        }
    }
}
