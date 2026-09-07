// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Tenancy;
using Microsoft.AspNetCore.DataProtection;

namespace Workbench.Server.Identity;

public sealed class IdentityOperationService(
    string connectionString,
    IIdentityMessageDelivery delivery,
    ISensitiveRequestRateLimiter rateLimiter,
    UserManager<WorkbenchUser> userManager,
    TimeProvider timeProvider,
    TenantContextProof tenantContextProof,
    IDataProtectionProvider dataProtection,
    IConfiguration configuration,
    IHostEnvironment environment,
    IHttpContextAccessor httpContext)
{
    public bool PublicOperationsAvailable => delivery.IsAvailable && rateLimiter.IsAvailable &&
        configuration.GetValue("Identity:PublicRecoveryEnabled", environment.IsDevelopment());

    public bool PublicInvitationsAvailable => delivery.IsAvailable && rateLimiter.IsAvailable &&
        configuration.GetValue("Identity:PublicInvitationEnabled", environment.IsDevelopment());

    public async Task RequestRecoveryAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        if (!PublicOperationsAvailable ||
            !await AcquireAsync("recovery-request", normalizedEmail, cancellationToken))
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationId = Guid.NewGuid();
        await using var command = new SqlCommand("""
            INSERT INTO [Identity].[IdentityOperations]
                ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                 [CreatedAtUtc], [ExpiresAtUtc])
            VALUES
                (@id, @tenantId, @userId, 1, @tokenHash, @securityVersion, @now, @expires);
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", operationId);
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
        var message = new IdentityMessage(IdentityOperationPurpose.PasswordRecovery, target.Value.Email, token, expires);
        await EnqueueAsync(connection, transaction, target.Value.TenantId, operationId, message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (delivery is DevelopmentIdentityMessageDelivery)
        {
            await delivery.DeliverAsync(message, cancellationToken);
        }
    }

    public async Task<bool> RequestInvitationAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken)
    {
        if (!PublicInvitationsAvailable || string.IsNullOrWhiteSpace(email) || email.Length > 256)
        {
            return false;
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        if (!await AcquireAsync("invitation-request", $"{tenantId:N}:{normalizedEmail}", cancellationToken))
        {
            return false;
        }
        var token = SessionToken.Create();
        var now = timeProvider.GetUtcNow();
        var expires = now.AddHours(24);
        await using var connection = await OpenTenantConnectionAsync(tenantId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationId = Guid.NewGuid();
        await using var command = new SqlCommand("[Identity].[CreateInvitation]", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@UserId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@OperationId", operationId);
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

        var message = new IdentityMessage(IdentityOperationPurpose.Invitation, email.Trim(), token, expires);
        await EnqueueAsync(connection, transaction, tenantId, operationId, message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (delivery is DevelopmentIdentityMessageDelivery)
        {
            await delivery.DeliverAsync(message, cancellationToken);
        }
        return true;
    }

    private async Task EnqueueAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId,
        Guid operationId, IdentityMessage message, CancellationToken cancellationToken)
    {
        // The explicit local sink is non-delivering and deliberately holds messages
        // only in memory for development. Real providers always use the SQL outbox.
        if (delivery is DevelopmentIdentityMessageDelivery)
        {
            return;
        }
        var id = Guid.NewGuid();
        var payload = dataProtection.CreateProtector("Workbench.IdentityOutbox.v1", tenantId.ToString("N"), id.ToString("N"))
            .Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message));
        await using var command = new SqlCommand("""
            INSERT INTO [Operations].[WorkItems]
                ([Id], [TenantId], [Kind], [IdentityOperationId], [ProtectedPayload], [CreatedAtUtc], [AvailableAtUtc])
            VALUES (@id, @tenant, 2, @operation, @payload, SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@tenant", tenantId);
        command.Parameters.AddWithValue("@operation", operationId);
        command.Parameters.Add(new SqlParameter("@payload", SqlDbType.VarBinary, 8000) { Value = payload });
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        if (!(purpose == IdentityOperationPurpose.Invitation ? PublicInvitationsAvailable : PublicOperationsAvailable) ||
            !await AcquireAsync(purpose == IdentityOperationPurpose.Invitation ? "invitation-consume" : "recovery-consume",
                token, cancellationToken))
        {
            return false;
        }
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
        if (purpose == IdentityOperationPurpose.Invitation)
        {
            await using var claim = new SqlCommand("[Identity].[ClaimInvitationIdentity]", connection,
                (SqlTransaction)transaction)
            { CommandType = CommandType.StoredProcedure };
            claim.Parameters.AddWithValue("@TenantId", authority.Value.TenantId);
            claim.Parameters.AddWithValue("@UserId", userId);
            claim.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
            claim.Parameters.AddWithValue("@Now", now);
            try
            {
                await claim.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException error) when (error.Number is 2601 or 2627 or 50004)
            {
                return false;
            }
        }
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

    private async Task<bool> AcquireAsync(string operation, string subject, CancellationToken cancellationToken)
    {
        var address = httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!await rateLimiter.TryAcquireAsync($"{operation}:network:{address}", cancellationToken))
        {
            return false;
        }
        return await rateLimiter.TryAcquireAsync($"{operation}:subject:{subject}", cancellationToken);
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
