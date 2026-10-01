// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;
using Workbench.Server.ServiceAdministration;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminSessionTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _application = null!;
    private ServiceAdminSessionService _sessions = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        await _application.ProvisionServiceAdminAsync();
        _sessions = new ServiceAdminSessionService(_application.WebConnectionString, new SessionOptions(), new PasswordHasher<ServiceAdminAccount>());
    }

    public ValueTask DisposeAsync() => _application.DisposeAsync();
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Theory]
    [InlineData("DisableServiceAdmin")]
    [InlineData("ResetServiceAdminPassword")]
    [InlineData("RevokeServiceAdminSessions")]
    public async Task VerifiedCredentialsCannotIssueAfterOperatorAuthorityChanges(string operation)
    {
        // GIVEN credentials verified before a concurrent operator change
        var verified = await _sessions.VerifyAsync("  ADMIN@example.com  ", AuthTestApplication.ServiceAdminPassword, CancellationToken.None);
        Assert.NotNull(verified);
        // WHEN the account is disabled, reset or revoked between verification and issuance
        await _application.MaintainServiceAdminAsync(operation);
        // THEN the adapter carries the verified version and issuance fails closed
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sessions.CreateAsync(verified, Now, CancellationToken.None));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(720, false)]
    public async Task ResolutionUsesTheSuppliedClockAndRejectsExpiryBoundary(int minutes, bool allowed)
    {
        // GIVEN a session with thirty-minute idle and twelve-hour absolute lifetime
        var verified = await _sessions.VerifyAsync(AuthTestApplication.AdminEmail, AuthTestApplication.ServiceAdminPassword, CancellationToken.None);
        var created = await _sessions.CreateAsync(verified!, Now, CancellationToken.None);
        // WHEN resolving at the clock boundary THEN SQL receives the actual supplied timestamp
        var resolved = await _sessions.ResolveAsync(created.Token, Now.AddMinutes(minutes), CancellationToken.None);
        Assert.Equal(allowed, resolved is not null);
        if (allowed)
        {
            Assert.Equal(created.Id, resolved!.SessionId);
            Assert.Equal(AuthTestApplication.ServiceAdminId, resolved.AccountId);
        }
    }

    [Fact]
    public async Task LegacyPasswordRehashIsDurableAndRemainsCompatible()
    {
        // GIVEN a valid legacy hash that the configured production hasher must upgrade
        var account = new ServiceAdminAccount();
        var legacy = new PasswordHasher<ServiceAdminAccount>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
        { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 })).HashPassword(account, AuthTestApplication.ServiceAdminPassword);
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using (var write = new SqlCommand("UPDATE ServiceAdministration.Accounts SET PasswordHash=@hash WHERE Id=@id", connection))
        {
            write.Parameters.AddWithValue("@hash", legacy);
            write.Parameters.AddWithValue("@id", AuthTestApplication.ServiceAdminId);
            await write.ExecuteNonQueryAsync();
        }
        // WHEN credentials are verified through the real runtime procedure boundary
        var verified = await _sessions.VerifyAsync(AuthTestApplication.AdminEmail, AuthTestApplication.ServiceAdminPassword, CancellationToken.None);
        Assert.NotNull(verified);
        // THEN the upgraded hash is durable and valid at the current strength
        await using var read = new SqlCommand("SELECT PasswordHash FROM ServiceAdministration.Accounts WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", AuthTestApplication.ServiceAdminId);
        var upgraded = (string)(await read.ExecuteScalarAsync())!;
        Assert.NotEqual(legacy, upgraded);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<ServiceAdminAccount>().VerifyHashedPassword(account, upgraded, AuthTestApplication.ServiceAdminPassword));
    }
}
