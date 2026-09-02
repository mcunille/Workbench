// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Identity;

public sealed class BuiltInPasswordVerifier : IIdentityVerifier
{
    public const string Scheme = "BuiltInPassword";

    private readonly string _connectionString;
    private readonly IPasswordHasher<WorkbenchUser> _passwordHasher;
    private readonly WorkbenchUser _dummyUser = new()
    {
        Id = Guid.Empty,
        TenantId = Guid.Empty,
        UserName = "dummy",
        NormalizedUserName = "DUMMY",
        SecurityStamp = "dummy",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        State = AccountState.Disabled,
    };
    private readonly string _dummyPasswordHash;

    public BuiltInPasswordVerifier(
        string connectionString,
        IPasswordHasher<WorkbenchUser> passwordHasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _passwordHasher = passwordHasher;
        _dummyPasswordHash = passwordHasher.HashPassword(_dummyUser, "dummy-password-never-accepted");
    }

    public async Task<VerifiedIdentity?> VerifyAsync(
        string email,
        string credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(credential);

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await ResolveUserAsync(normalizedEmail, cancellationToken);
        var hash = user?.PasswordHash ?? _dummyPasswordHash;
        var result = _passwordHasher.VerifyHashedPassword(user ?? _dummyUser, hash, credential);

        if (user is null ||
            user.State is not AccountState.Enabled ||
            result is PasswordVerificationResult.Failed)
        {
            return null;
        }

        return new VerifiedIdentity(Scheme, user.Id.ToString("N"), user.Id, user.TenantId);
    }

    private async Task<WorkbenchUser?> ResolveUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[Identity].[ResolveCredential]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SqlParameter("@NormalizedEmail", SqlDbType.NVarChar, 256)
        {
            Value = normalizedEmail,
        });

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkbenchUser
        {
            Id = reader.GetGuid(0),
            TenantId = reader.GetGuid(1),
            PasswordHash = reader.IsDBNull(2) ? null : reader.GetString(2),
            SecurityStamp = reader.IsDBNull(3) ? null : reader.GetString(3),
            State = (AccountState)reader.GetInt32(4),
            SecurityVersion = reader.GetInt64(5),
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(6),
        };
    }
}
