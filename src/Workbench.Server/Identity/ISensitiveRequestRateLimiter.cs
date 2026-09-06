// Copyright (c) 2026 The White Stag Collection.

using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Workbench.Server.Tenancy;

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
        foreach (var expired in _requests.Where(request => now - request.Value.Window >= TimeSpan.FromMinutes(5)))
        {
            _requests.TryRemove(expired);
        }
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

public sealed class SqlSensitiveRequestRateLimiter(string connectionString, TenantContextProof proof) : ISensitiveRequestRateLimiter
{
    public bool IsAvailable => true;

    public async ValueTask<bool> TryAcquireAsync(
        string partition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var partitionHash = proof.HashRateLimitPartition(partition);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = new SqlCommand("[Security].[TryAcquireSensitiveRequest]", connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            command.Parameters.Add(new SqlParameter("@PartitionHash", SqlDbType.Binary, 32)
            {
                Value = partitionHash,
            });
            return Convert.ToBoolean(await command.ExecuteScalarAsync(deadline.Token));
        }
        catch (Exception error) when (error is SqlException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new Operations.DependencyUnavailableException();
        }
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
