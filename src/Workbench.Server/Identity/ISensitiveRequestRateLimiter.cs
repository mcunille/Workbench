// Copyright (c) 2026 The White Stag Collection.

using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Identity;

public interface ISensitiveRequestRateLimiter
{
    bool IsAvailable { get; }

    ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken);
}

public sealed class DevelopmentSensitiveRequestRateLimiter(TimeProvider timeProvider)
    : ISensitiveRequestRateLimiter
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Window, int Count)> _requests = new();

    public bool IsAvailable => true;

    public ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entry = _requests.AddOrUpdate(
            partition,
            _ => (now, 1),
            (_, current) => now - current.Window >= TimeSpan.FromMinutes(1)
                ? (now, 1)
                : (current.Window, current.Count + 1));
        return ValueTask.FromResult(entry.Count <= 5);
    }
}

public sealed class DisabledSensitiveRequestRateLimiter : ISensitiveRequestRateLimiter
{
    public bool IsAvailable => false;

    public ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}

public sealed class SqlSensitiveRequestRateLimiter(
    string connectionString,
    TimeProvider timeProvider) : ISensitiveRequestRateLimiter
{
    public bool IsAvailable => true;

    public async ValueTask<bool> TryAcquireAsync(
        string partition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var partitionHash = SHA256.HashData(Encoding.UTF8.GetBytes(partition));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Security].[TryAcquireSensitiveRequest]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SqlParameter("@PartitionHash", SqlDbType.Binary, 32)
        {
            Value = partitionHash,
        });
        command.Parameters.AddWithValue("@Now", timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("@WindowSeconds", 60);
        command.Parameters.AddWithValue("@PermitLimit", 5);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }
}

public static class SensitiveRequestPartitions
{
    public static string LoginAccount(string email) =>
        Hash($"login-account:{email.Trim().ToUpperInvariant()}");

    public static string LoginNetwork(string address) =>
        Hash($"login-network:{address}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
