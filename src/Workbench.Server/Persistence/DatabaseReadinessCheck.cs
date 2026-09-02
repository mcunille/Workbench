// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Workbench.Server.Persistence;

public sealed class DatabaseReadinessCheck(string connectionString) : IHealthCheck
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
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Database security state is missing.");
            }

            var state = new DatabaseSecurityState(
                reader.GetBoolean(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetInt64(4),
                reader.GetInt64(5));
            return state.IsReady
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database security state is not ready.");
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Database readiness check failed.");
        }
    }
}
