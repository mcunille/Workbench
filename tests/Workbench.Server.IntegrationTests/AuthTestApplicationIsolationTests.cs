// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuthTestApplicationIsolationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ApplicationsHaveIndependentDataCredentialsAndCurrentSchema()
    {
        // GIVEN two independently prepared applications with identical test identities.
        await using var first = await AuthTestApplication.CreateAsync(sqlServer);
        await using var second = await AuthTestApplication.CreateAsync(sqlServer);
        await using var firstDb = Context(first);
        await using var secondDb = Context(second);
        var firstUsers = await firstDb.Users.IgnoreQueryFilters().OrderBy(user => user.Id).ToArrayAsync();
        Assert.Equal(4, firstUsers.Length);
        Assert.NotEqual(new SqlConnectionStringBuilder(first.WebConnectionString).UserID,
            new SqlConnectionStringBuilder(second.WebConnectionString).UserID);

        // WHEN one application changes its admin's identity and credential.
        var admin = firstUsers.Single(user => user.Id == AuthTestApplication.AdminUserId);
        admin.Email = "changed@example.test";
        admin.PasswordHash = new PasswordHasher<WorkbenchUser>().HashPassword(admin, "different password");
        await firstDb.SaveChangesAsync();

        // THEN the other keeps valid seeded passwords for every user and independent security stamps.
        var secondUsers = await secondDb.Users.IgnoreQueryFilters().AsNoTracking().OrderBy(user => user.Id).ToArrayAsync();
        Assert.Equal(4, secondUsers.Length);
        var hasher = new PasswordHasher<WorkbenchUser>();
        foreach (var user in secondUsers)
        {
            Assert.Equal(PasswordVerificationResult.Success,
                hasher.VerifyHashedPassword(user, user.PasswordHash!, AuthTestApplication.AdminPassword));
            Assert.Equal(PasswordVerificationResult.Failed,
                hasher.VerifyHashedPassword(user, user.PasswordHash!, "wrong password"));
            Assert.NotEqual(firstUsers.Single(firstUser => firstUser.Id == user.Id).SecurityStamp, user.SecurityStamp);
        }
        Assert.Equal(AuthTestApplication.AdminEmail,
            secondUsers.Single(user => user.Id == admin.Id).Email);
        Assert.Empty(await secondDb.Database.GetPendingMigrationsAsync());
        Assert.Equal(await firstDb.Database.GetAppliedMigrationsAsync(), await secondDb.Database.GetAppliedMigrationsAsync());
        await using var firstConnection = new SqlConnection(first.AdminConnectionString);
        await using var secondConnection = new SqlConnection(second.AdminConnectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstKey = new SqlCommand("SELECT ProofKey FROM Security.TenantContextKeys WHERE Id = 1", firstConnection);
        await using var secondKey = new SqlCommand("SELECT ProofKey FROM Security.TenantContextKeys WHERE Id = 1", secondConnection);
        var firstProof = (byte[])(await firstKey.ExecuteScalarAsync())!;
        var secondProof = (byte[])(await secondKey.ExecuteScalarAsync())!;
        Assert.False(firstProof.SequenceEqual(secondProof));

        // AND a contained credential from the other database cannot authenticate here.
        var wrongDatabase = new SqlConnectionStringBuilder(second.WebConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(first.WebConnectionString).InitialCatalog,
            ConnectTimeout = 2,
        };
        await using var rejected = new SqlConnection(wrongDatabase.ConnectionString);
        var error = await Assert.ThrowsAsync<SqlException>(() => rejected.OpenAsync());
        Assert.Contains(error.Number, new[] { 18456, 4060 });
    }

    [Fact]
    public async Task FailedPriorMigrationLeavesNoDatabaseBehind()
    {
        // GIVEN a disposable SQL instance and an invalid requested schema version.
        await using var observer = await sqlServer.CreateDatabaseAsync();
        await using var connection = new SqlConnection(observer.AdminConnectionString);
        await connection.OpenAsync();
        await using var count = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'workbench_test_%'", connection);
        var before = (int)(await count.ExecuteScalarAsync())!;
        // WHEN preparing that schema fails THEN the original migration error is surfaced.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sqlServer.CreateMigratedDatabaseAsync("MissingTestMigration"));
        // AND its partially prepared database has been removed.
        Assert.Equal(before, (int)(await count.ExecuteScalarAsync())!);
    }

    private static WorkbenchDbContext Context(AuthTestApplication application) => new(
        new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(application.AdminConnectionString).Options,
        TenantContext.None);
}
