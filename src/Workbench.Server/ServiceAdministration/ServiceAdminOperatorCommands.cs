// Copyright (c) 2026 The White Stag Collection.

using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;

namespace Workbench.Server.ServiceAdministration;

public sealed class ServiceAdminOperatorCommands(
    string connectionString,
    IPasswordHasher<ServiceAdminAccount> passwordHasher,
    TimeProvider timeProvider)
{
    public async Task<Guid> ProvisionAsync(string email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        email = email.Trim();
        if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email))
            throw new ArgumentException("The service-admin email is invalid.", nameof(email));
        WorkbenchPasswordPolicy.EnsureValid(password, nameof(password));
        var account = new ServiceAdminAccount
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            SecurityVersion = 1,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        var hash = passwordHasher.HashPassword(account, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = Command("ProvisionServiceAdmin", account.Id, account.CreatedAtUtc, connection);
        command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, -1) { Value = account.Email });
        command.Parameters.Add(new SqlParameter("@NormalizedEmail", SqlDbType.NVarChar, -1) { Value = account.NormalizedEmail });
        command.Parameters.Add(new SqlParameter("@PasswordHash", SqlDbType.NVarChar, -1) { Value = hash });
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The service-admin account was not provisioned."));
    }

    public Task DisableAsync(Guid accountId, CancellationToken cancellationToken) =>
        MaintainAsync("DisableServiceAdmin", accountId, null, cancellationToken);

    public Task RevokeSessionsAsync(Guid accountId, CancellationToken cancellationToken) =>
        MaintainAsync("RevokeServiceAdminSessions", accountId, null, cancellationToken);

    public Task ResetPasswordAsync(Guid accountId, string password, CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        WorkbenchPasswordPolicy.EnsureValid(password, nameof(password));
        var hash = passwordHasher.HashPassword(new ServiceAdminAccount { Id = accountId }, password);
        return MaintainAsync("ResetServiceAdminPassword", accountId, hash, cancellationToken);
    }

    private async Task MaintainAsync(string name, Guid accountId, string? passwordHash, CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = Command(name, accountId, timeProvider.GetUtcNow(), connection);
        if (passwordHash is not null)
            command.Parameters.Add(new SqlParameter("@PasswordHash", SqlDbType.NVarChar, -1) { Value = passwordHash });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateAccountId(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("The service-admin account identifier is invalid.", nameof(accountId));
    }

    private static SqlCommand Command(string name, Guid accountId, DateTimeOffset now, SqlConnection connection)
    {
        var command = new SqlCommand("Administration." + name, connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@AccountId", accountId);
        command.Parameters.AddWithValue("@Now", now);
        return command;
    }
}
