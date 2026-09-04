// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public sealed class IdentityOperationService(
    string connectionString,
    IIdentityMessageDelivery delivery,
    ISensitiveRequestRateLimiter rateLimiter,
    UserManager<WorkbenchUser> userManager,
    TimeProvider timeProvider,
    TenantContextProof tenantContextProof)
{
    public bool PublicOperationsAvailable => delivery.IsAvailable && rateLimiter.IsAvailable;

    public async Task RequestRecoveryAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var partition = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        if (!PublicOperationsAvailable ||
            !await rateLimiter.TryAcquireAsync(partition, cancellationToken))
        {
            return;
        }

        var target = await ResolveRecoveryTargetAsync(normalizedEmail, cancellationToken);
        if (target is null)
        {
            return;
        }

        var token = SessionToken.Create();
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(30);
        await using var connection = await OpenTenantConnectionAsync(target.Value.TenantId, cancellationToken);
        await using var command = new SqlCommand("""
            INSERT INTO [Identity].[IdentityOperations]
                ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                 [CreatedAtUtc], [ExpiresAtUtc])
            VALUES
                (@id, @tenantId, @userId, 1, @tokenHash, @securityVersion, @now, @expires);
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@tenantId", target.Value.TenantId);
        command.Parameters.AddWithValue("@userId", target.Value.UserId);
        command.Parameters.Add(new SqlParameter("@tokenHash", SqlDbType.Binary, 32)
        {
            Value = SessionToken.Hash(token),
        });
        command.Parameters.AddWithValue("@securityVersion", target.Value.SecurityVersion);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@expires", expires);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await delivery.DeliverAsync(
            new IdentityMessage(IdentityOperationPurpose.PasswordRecovery, target.Value.Email, token, expires),
            cancellationToken);
    }

    public async Task<bool> RequestInvitationAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken)
    {
        if (!PublicOperationsAvailable || string.IsNullOrWhiteSpace(email) || email.Length > 256)
        {
            return false;
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var token = SessionToken.Create();
        var now = timeProvider.GetUtcNow();
        var expires = now.AddHours(24);
        await using var connection = await OpenTenantConnectionAsync(tenantId, cancellationToken);
        await using var command = new SqlCommand("[Identity].[CreateInvitation]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@UserId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@OperationId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@Email", email.Trim());
        command.Parameters.AddWithValue("@NormalizedEmail", normalizedEmail);
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32)
        {
            Value = SessionToken.Hash(token),
        });
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@Expires", expires);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException error) when (error.Number is 2601 or 2627)
        {
            return false;
        }

        await delivery.DeliverAsync(
            new IdentityMessage(IdentityOperationPurpose.Invitation, email.Trim(), token, expires),
            cancellationToken);
        return true;
    }

    public Task<bool> ConsumeRecoveryAsync(
        string token,
        string newPassword,
        string correlationId,
        CancellationToken cancellationToken) =>
        ConsumeAsync(token, newPassword, IdentityOperationPurpose.PasswordRecovery, correlationId, cancellationToken);

    public Task<bool> ConsumeInvitationAsync(
        string token,
        string newPassword,
        string correlationId,
        CancellationToken cancellationToken) =>
        ConsumeAsync(token, newPassword, IdentityOperationPurpose.Invitation, correlationId, cancellationToken);

    private async Task<bool> ConsumeAsync(
        string token,
        string newPassword,
        IdentityOperationPurpose purpose,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!SessionToken.TryHash(token, out var tokenHash))
        {
            return false;
        }

        var authority = await ResolveOperationAuthorityAsync(tokenHash, cancellationToken);
        if (authority is null)
        {
            return false;
        }

        await using var connection = await OpenTenantConnectionAsync(authority.Value.TenantId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var read = new SqlCommand("""
            SELECT TOP (1)
                [operation].[Id], [operation].[UserId], [operation].[SecurityVersion],
                [operation].[ExpiresAtUtc], [operation].[ConsumedAtUtc],
                [user].[SecurityVersion], [user].[State], [user].[PasswordHash]
            FROM [Identity].[IdentityOperations] AS [operation] WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [Identity].[Users] AS [user] WITH (UPDLOCK, HOLDLOCK)
                ON [user].[TenantId] = [operation].[TenantId]
                AND [user].[Id] = [operation].[UserId]
            WHERE [operation].[TokenHash] = @tokenHash AND [operation].[Purpose] = @purpose;
            """, connection, (SqlTransaction)transaction);
        read.Parameters.Add(new SqlParameter("@tokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
        read.Parameters.AddWithValue("@purpose", (int)purpose);
        Guid operationId;
        Guid userId;
        long operationVersion;
        DateTimeOffset expiresAt;
        DateTimeOffset? consumedAt;
        long userVersion;
        AccountState state;
        string? existingHash;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            operationId = reader.GetGuid(0);
            userId = reader.GetGuid(1);
            operationVersion = reader.GetInt64(2);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(3);
            consumedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
            userVersion = reader.GetInt64(5);
            state = (AccountState)reader.GetInt32(6);
            existingHash = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        var now = timeProvider.GetUtcNow();
        var expectedState = purpose is IdentityOperationPurpose.Invitation
            ? AccountState.Invited
            : AccountState.Enabled;
        if (consumedAt.HasValue || now >= expiresAt || operationVersion != userVersion ||
            state != expectedState)
        {
            return false;
        }

        var user = new WorkbenchUser
        {
            Id = userId,
            TenantId = authority.Value.TenantId,
            PasswordHash = existingHash,
            SecurityVersion = userVersion,
            State = state,
            CreatedAtUtc = now,
        };
        foreach (var validator in userManager.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(userManager, user, newPassword)).Succeeded)
            {
                return false;
            }
        }

        var newHash = userManager.PasswordHasher.HashPassword(user, newPassword);
        await using var update = new SqlCommand("""
            UPDATE [Identity].[Users]
            SET [PasswordHash] = @passwordHash,
                [SecurityStamp] = @securityStamp,
                [ConcurrencyStamp] = @concurrencyStamp,
                [State] = CASE WHEN @purpose = 2 THEN 1 ELSE [State] END,
                [SecurityVersion] = [SecurityVersion] + 1
            WHERE [Id] = @userId;

            UPDATE [Identity].[Sessions]
            SET [RevokedAtUtc] = @now, [RevocationReason] = N'PasswordRecovered'
            WHERE [UserId] = @userId AND [RevokedAtUtc] IS NULL;

            UPDATE [Identity].[IdentityOperations]
            SET [ConsumedAtUtc] = @now
            WHERE [Id] = @operationId AND [ConsumedAtUtc] IS NULL;

            INSERT INTO [Security].[TenantSecurityAuditEvents]
                ([Id], [TenantId], [Action], [ActorUserId], [TargetType], [TargetId],
                 [Outcome], [CorrelationId], [OccurredAtUtc])
            VALUES
                (NEWID(), @tenantId, @auditAction, NULL, N'User', @userId,
                 N'Succeeded', @correlationId, @now);
            """, connection, (SqlTransaction)transaction);
        update.Parameters.AddWithValue("@passwordHash", newHash);
        update.Parameters.AddWithValue("@securityStamp", Guid.NewGuid().ToString("N"));
        update.Parameters.AddWithValue("@concurrencyStamp", Guid.NewGuid().ToString("N"));
        update.Parameters.AddWithValue("@now", now);
        update.Parameters.AddWithValue("@userId", userId);
        update.Parameters.AddWithValue("@operationId", operationId);
        update.Parameters.AddWithValue("@purpose", (int)purpose);
        update.Parameters.AddWithValue(
            "@auditAction",
            purpose is IdentityOperationPurpose.Invitation
                ? "identity.invitation.consumed"
                : "identity.recovery.consumed");
        update.Parameters.AddWithValue("@tenantId", authority.Value.TenantId);
        update.Parameters.AddWithValue("@correlationId", correlationId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<(Guid UserId, Guid TenantId, long SecurityVersion, string Email)?>
        ResolveRecoveryTargetAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Identity].[ResolveRecoveryTarget]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@NormalizedEmail", normalizedEmail);
        var proof = tenantContextProof.CreateRecoveryLookupProof(normalizedEmail);
        command.Parameters.Add(new SqlParameter("@Nonce", SqlDbType.Binary, TenantContextProof.KeySize)
        {
            Value = proof.Nonce,
        });
        command.Parameters.Add(new SqlParameter("@Proof", SqlDbType.Binary, TenantContextProof.KeySize)
        {
            Value = proof.Proof,
        });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private async Task<(Guid TenantId, Guid UserId)?> ResolveOperationAuthorityAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Identity].[ResolveOperationAuthority]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    private async Task<SqlConnection> OpenTenantConnectionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await tenantContextProof.ApplyAsync(connection, tenantId, cancellationToken);
        return connection;
    }
}
