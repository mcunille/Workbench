// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Operations;

public sealed record WorkQueueStatus(long PendingCount, long OldestPendingAgeSeconds);

public static class WorkQueueTelemetry
{
    public static async Task<WorkQueueStatus> ReadAsync(string workerConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(workerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Operations].[ReadWorkQueueStatus]", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 5,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Queue status was unavailable.");
        }
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }
}
