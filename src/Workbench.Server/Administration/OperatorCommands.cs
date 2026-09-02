// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;

namespace Workbench.Server.Administration;

public sealed class BootstrapAlreadyCompletedException : InvalidOperationException
{
    public BootstrapAlreadyCompletedException()
        : base("Initial bootstrap has already been completed.")
    {
    }
}

public sealed class OperatorCommands(
    string connectionString,
    IPasswordHasher<WorkbenchUser> passwordHasher,
    TimeProvider timeProvider)
{
    public async Task<string?> CreateDevelopmentRecoveryAsync(
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var token = SessionToken.Create();
        var now = timeProvider.GetUtcNow();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Administration].[CreateDevelopmentRecovery]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@OperationId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@NormalizedEmail", email.Trim().ToUpperInvariant());
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32)
        {
            Value = SessionToken.Hash(token),
        });
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@ExpiresAtUtc", now.AddMinutes(30));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)) ? token : null;
    }

    public Task<Guid> BootstrapAsync(
        string tenantName,
        string administratorEmail,
        string administratorPassword,
        CancellationToken cancellationToken) =>
        CreateTenantAsync(
            tenantName,
            administratorEmail,
            administratorPassword,
            requireEmptyDatabase: true,
            cancellationToken);

    public Task<Guid> CreateAdditionalTenantAsync(
        string tenantName,
        string administratorEmail,
        string administratorPassword,
        CancellationToken cancellationToken) =>
        CreateTenantAsync(
            tenantName,
            administratorEmail,
            administratorPassword,
            requireEmptyDatabase: false,
            cancellationToken);

    public async Task SanitizeRestoreAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (correlationId.Length > 100)
        {
            throw new ArgumentException("The correlation identifier is too long.", nameof(correlationId));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Administration].[SanitizeRestore]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@Now", timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("@CorrelationId", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Guid> CreateTenantAsync(
        string tenantName,
        string administratorEmail,
        string administratorPassword,
        bool requireEmptyDatabase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantName);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorPassword);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var user = new WorkbenchUser
        {
            Id = userId,
            TenantId = tenantId,
            UserName = administratorEmail.Trim(),
            NormalizedUserName = administratorEmail.Trim().ToUpperInvariant(),
            Email = administratorEmail.Trim(),
            NormalizedEmail = administratorEmail.Trim().ToUpperInvariant(),
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        var passwordHash = passwordHasher.HashPassword(user, administratorPassword);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Administration].[ProvisionTenant]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@TenantName", tenantName.Trim());
        command.Parameters.AddWithValue("@NormalizedTenantName", tenantName.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@Email", administratorEmail.Trim());
        command.Parameters.AddWithValue("@NormalizedEmail", administratorEmail.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@SecurityStamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ConcurrencyStamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@AdministratorRoleId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@MemberRoleId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@Now", timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("@RequireEmptyDatabase", requireEmptyDatabase);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException error) when (error.Number == 50010)
        {
            throw new BootstrapAlreadyCompletedException();
        }

        return tenantId;
    }
}
