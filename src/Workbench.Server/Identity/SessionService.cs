// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Identity;

public sealed record CreatedSession(
    Guid Id,
    string Token,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc);

public sealed record ResolvedSession(
    Guid SessionId,
    Guid UserId,
    Guid TenantId,
    string? Email,
    IReadOnlySet<string> Permissions);

public sealed class SessionService
{
    private readonly string _connectionString;
    private readonly SessionOptions _options;

    public SessionService(string connectionString, SessionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.Validate();
        _connectionString = connectionString;
        _options = options;
    }

    public async Task<CreatedSession> CreateAsync(
        VerifiedIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.CreateVersion7();
        var token = SessionToken.Create();
        var tokenHash = SessionToken.Hash(token);
        var idleExpiresAt = now.Add(_options.IdleTimeout);
        var absoluteExpiresAt = now.Add(_options.AbsoluteLifetime);

        await using var connection = await OpenTenantConnectionAsync(identity.TenantId, cancellationToken);
        await using var command = new SqlCommand("""
            INSERT INTO [Identity].[Sessions]
                ([Id], [TenantId], [UserId], [TokenHash], [SecurityVersion],
                 [CreatedAtUtc], [LastSeenAtUtc], [IdleExpiresAtUtc], [AbsoluteExpiresAtUtc])
            SELECT
                @sessionId, @tenantId, [user].[Id], @tokenHash, [user].[SecurityVersion],
                @now, @now, @idleExpiresAt, @absoluteExpiresAt
            FROM [Identity].[Users] AS [user]
            INNER JOIN [Tenancy].[Tenants] AS [tenant]
                ON [tenant].[Id] = [user].[TenantId]
            WHERE [user].[Id] = @userId
                AND [user].[TenantId] = @tenantId
                AND [user].[State] = 1
                AND [tenant].[IsEnabled] = 1;
            """, connection);
        command.Parameters.Add(new SqlParameter("@sessionId", SqlDbType.UniqueIdentifier) { Value = sessionId });
        command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier) { Value = identity.TenantId });
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = identity.UserId });
        command.Parameters.Add(new SqlParameter("@tokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        command.Parameters.Add(new SqlParameter("@idleExpiresAt", SqlDbType.DateTimeOffset) { Value = idleExpiresAt });
        command.Parameters.Add(new SqlParameter("@absoluteExpiresAt", SqlDbType.DateTimeOffset) { Value = absoluteExpiresAt });

        if (await command.ExecuteNonQueryAsync(cancellationToken) is not 1)
        {
            throw new InvalidOperationException("The verified identity is no longer eligible for a session.");
        }

        return new CreatedSession(sessionId, token, idleExpiresAt, absoluteExpiresAt);
    }

    public async Task<ResolvedSession?> ResolveAsync(
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!SessionToken.TryHash(token, out var tokenHash))
        {
            return null;
        }
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Identity].[ResolveSession]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32) { Value = tokenHash });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var sessionId = reader.GetGuid(0);
        var tenantId = reader.GetGuid(1);
        var userId = reader.GetGuid(2);
        var sessionSecurityVersion = reader.GetInt64(3);
        var lastSeenAt = reader.GetFieldValue<DateTimeOffset>(4);
        var idleExpiresAt = reader.GetFieldValue<DateTimeOffset>(5);
        var absoluteExpiresAt = reader.GetFieldValue<DateTimeOffset>(6);
        var revokedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
        var userSecurityVersion = reader.GetInt64(8);
        var userState = (AccountState)reader.GetInt32(9);
        var tenantEnabled = reader.GetBoolean(10);
        var email = reader.IsDBNull(11) ? null : reader.GetString(11);

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                permissions.Add(reader.GetString(0));
            }
        }

        if (revokedAt.HasValue ||
            userState is not AccountState.Enabled ||
            !tenantEnabled ||
            sessionSecurityVersion != userSecurityVersion ||
            now >= idleExpiresAt ||
            now >= absoluteExpiresAt)
        {
            return null;
        }

        if (now - lastSeenAt >= _options.LastSeenUpdateInterval)
        {
            await TouchAsync(tenantId, sessionId, now, absoluteExpiresAt, cancellationToken);
        }

        return new ResolvedSession(sessionId, userId, tenantId, email, permissions);
    }

    public Task RevokeAsync(
        Guid tenantId,
        Guid sessionId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RevokeWhereAsync(tenantId, "[Id] = @targetId", sessionId, reason, now, cancellationToken);

    public Task RevokeUserSessionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RevokeWhereAsync(
            tenantId,
            "[Id] = @targetId",
            sessionId,
            reason,
            now,
            cancellationToken,
            userId);

    public Task RevokeAllAsync(
        Guid tenantId,
        Guid userId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RevokeWhereAsync(tenantId, "[UserId] = @targetId", userId, reason, now, cancellationToken);

    private async Task TouchAsync(
        Guid tenantId,
        Guid sessionId,
        DateTimeOffset now,
        DateTimeOffset absoluteExpiresAt,
        CancellationToken cancellationToken)
    {
        var idleExpiresAt = now.Add(_options.IdleTimeout);
        if (idleExpiresAt > absoluteExpiresAt)
        {
            idleExpiresAt = absoluteExpiresAt;
        }

        await using var connection = await OpenTenantConnectionAsync(tenantId, cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE [Identity].[Sessions]
            SET [LastSeenAtUtc] = @now, [IdleExpiresAtUtc] = @idleExpiresAt
            WHERE [Id] = @sessionId AND [RevokedAtUtc] IS NULL;
            """, connection);
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        command.Parameters.Add(new SqlParameter("@idleExpiresAt", SqlDbType.DateTimeOffset) { Value = idleExpiresAt });
        command.Parameters.Add(new SqlParameter("@sessionId", SqlDbType.UniqueIdentifier) { Value = sessionId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RevokeWhereAsync(
        Guid tenantId,
        string predicate,
        Guid targetId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? requiredUserId = null)
    {
        if (reason.Length is 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await using var connection = await OpenTenantConnectionAsync(tenantId, cancellationToken);
        await using var command = new SqlCommand($"""
            UPDATE [Identity].[Sessions]
            SET [RevokedAtUtc] = @now, [RevocationReason] = @reason
            WHERE {predicate}
                AND (@requiredUserId IS NULL OR [UserId] = @requiredUserId)
                AND [RevokedAtUtc] IS NULL;
            """, connection);
        command.Parameters.Add(new SqlParameter("@targetId", SqlDbType.UniqueIdentifier) { Value = targetId });
        command.Parameters.Add(new SqlParameter("@requiredUserId", SqlDbType.UniqueIdentifier)
        {
            Value = requiredUserId.HasValue ? requiredUserId.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = reason });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenTenantConnectionAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand("""
                EXEC sys.sp_set_session_context
                    @key = N'TenantId', @value = @tenantId, @read_only = 1;
                """, connection);
            command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier) { Value = tenantId });
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
