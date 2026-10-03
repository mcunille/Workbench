// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;
using SessionOptions = Workbench.Server.Identity.SessionOptions;

namespace Workbench.Server.ServiceAdministration;

public sealed record VerifiedServiceAdmin(Guid AccountId, long SecurityVersion);
public sealed record ResolvedServiceAdminSession(Guid SessionId, Guid AccountId, string Email);

public sealed class ServiceAdminSessionService
{
    private readonly string _connectionString;
    private readonly SessionOptions _options;
    private readonly IPasswordHasher<ServiceAdminAccount> _hasher;
    private static readonly ServiceAdminAccount DummyAccount = new() { IsEnabled = false };
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<ServiceAdminAccount>().HashPassword(DummyAccount, "dummy-password-never-accepted"));

    public ServiceAdminSessionService(string connectionString, SessionOptions options, IPasswordHasher<ServiceAdminAccount> hasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.Validate();
        _connectionString = connectionString;
        _options = options;
        _hasher = hasher;
    }

    public async Task<VerifiedServiceAdmin?> VerifyAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        ServiceAdminAccount? account;
        await using (var command = Command("FindAccountForLogin", connection))
        {
            command.Parameters.Add(new SqlParameter("@NormalizedEmail", SqlDbType.NVarChar, -1) { Value = email.Trim().ToUpperInvariant() });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            account = await reader.ReadAsync(cancellationToken) ? new ServiceAdminAccount
            {
                Id = reader.GetGuid(0),
                Email = reader.GetString(1),
                NormalizedEmail = reader.GetString(2),
                PasswordHash = reader.GetString(3),
                IsEnabled = reader.GetBoolean(4),
                SecurityVersion = reader.GetInt64(5),
                CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(6),
            } : null;
        }
        var result = _hasher.VerifyHashedPassword(account ?? DummyAccount, account?.PasswordHash ?? DummyHash.Value, password);
        if (account is null || !account.IsEnabled || result == PasswordVerificationResult.Failed) return null;
        // Compatible older hashes authenticate without giving the web principal password-write authority.
        // Credential replacement, including upgrades, remains an audited operator reset.
        return new VerifiedServiceAdmin(account.Id, account.SecurityVersion);
    }

    public async Task<CreatedSession> CreateAsync(VerifiedServiceAdmin identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = new CreatedSession(Guid.CreateVersion7(), SessionToken.Create(), now.Add(_options.IdleTimeout), now.Add(_options.AbsoluteLifetime));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command("CreateSession", connection);
        command.Parameters.AddWithValue("@SessionId", session.Id);
        command.Parameters.AddWithValue("@AccountId", identity.AccountId);
        command.Parameters.AddWithValue("@SecurityVersion", identity.SecurityVersion);
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.VarBinary, -1) { Value = SessionToken.Hash(session.Token) });
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@IdleExpiresAtUtc", session.IdleExpiresAtUtc);
        command.Parameters.AddWithValue("@AbsoluteExpiresAtUtc", session.AbsoluteExpiresAtUtc);
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid created || created != session.Id)
            throw new InvalidOperationException("The verified service-admin identity is no longer eligible for a session.");
        return session;
    }

    public async Task<ResolvedServiceAdminSession?> ResolveAsync(string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!SessionToken.TryHash(token, out var hash)) return null;
        await using var connection = await OpenAsync(cancellationToken);
        // Shared-detail reads use Serializable, which survives on pooled connections.
        // Resolution owns an independent transaction with the original Read Committed
        // semantics; retain the procedure's explicit account and session locks.
        await using var command = new SqlCommand("""
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            EXEC ServiceAdministration.ResolveSession @TokenHash,@Now,@IdleTimeoutSeconds;
            """, connection);
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.VarBinary, -1) { Value = hash });
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@IdleTimeoutSeconds", checked((int)_options.IdleTimeout.TotalSeconds));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ResolvedServiceAdminSession(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)) : null;
    }

    public async Task RevokeAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command("RevokeSession", connection);
        command.Parameters.AddWithValue("@AccountId", accountId);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Command(string name, SqlConnection connection) => new("ServiceAdministration." + name, connection) { CommandType = CommandType.StoredProcedure };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
