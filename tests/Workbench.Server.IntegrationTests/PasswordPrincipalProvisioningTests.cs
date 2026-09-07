// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.Administration;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PasswordPrincipalProvisioningTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("GRANT CONTROL TO [workbench_web]")]
    [InlineData("GRANT CONTROL TO [workbench_operator]")]
    [InlineData("GRANT IMPERSONATE ON USER::dbo TO [workbench_web]")]
    [InlineData("GRANT IMPERSONATE ON USER::dbo TO [workbench_operator]")]
    [InlineData("GRANT ALTER ANY ROLE TO [workbench_web]")]
    [InlineData("GRANT CONTROL ON SCHEMA::[Identity] TO [workbench_web]")]
    [InlineData("GRANT CONTROL ON OBJECT::[Security].[ReadDatabaseReadiness] TO [workbench_web]")]
    [InlineData("GRANT EXECUTE ON OBJECT::[Administration].[ProvisionTenant] TO [workbench_web]")]
    [InlineData("GRANT SELECT ON OBJECT::[Security].[TenantContextKeys] ([ProofKey]) TO [workbench_web]")]
    [InlineData("GRANT SELECT ON OBJECT::[Identity].[Users] TO [workbench_web] WITH GRANT OPTION")]
    public async Task DestinationRoleGrantsAreRejectedBeforeProvisioning(string unsafeGrant)
    {
        // GIVEN a destination role with direct authority outside its migration-defined grants.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        await ExecuteAsync(database, unsafeGrant);
        var originalProof = await database.GetTenantContextProofKeyAsync();

        // WHEN distinct contained users are provisioned into those roles.
        var error = await Assert.ThrowsAsync<SqlException>(() => inputs.ProvisionAsync(database));

        // THEN provisioning rejects the role authority without creating users or changing the proof.
        Assert.Equal(50030, error.Number);
        Assert.Contains("incompatible direct authority", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_principals WHERE name IN ('web_user','operator_user','migrator_user')"));
        Assert.Equal(originalProof, await database.GetTenantContextProofKeyAsync());
    }

    [Theory]
    [InlineData("web_user")]
    [InlineData("WEB_USER")]
    public async Task DuplicateIdentitiesAreRejectedWithoutWrites(string migrator)
    {
        // GIVEN role names that identify the same SQL user.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        inputs.Principals[2] = inputs.Principals[2] with { User = migrator };
        // WHEN provisioning is attempted, THEN no user or proof changes survive.
        await Assert.ThrowsAnyAsync<Exception>(() => inputs.ProvisionAsync(database));
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_principals WHERE name IN ('web_user','operator_user')"));
    }

    [Theory]
    [InlineData("ALTER ROLE [workbench_migrator] ADD MEMBER [web_user]")]
    [InlineData("ALTER ROLE [db_owner] ADD MEMBER [web_user]")]
    [InlineData("CREATE ROLE [custom_role]; ALTER ROLE [workbench_migrator] ADD MEMBER [custom_role]; ALTER ROLE [custom_role] ADD MEMBER [web_user]")]
    [InlineData("GRANT CONTROL TO [web_user]")]
    [InlineData("GRANT IMPERSONATE ON USER::dbo TO [web_user]")]
    [InlineData("CREATE SCHEMA [owned_schema] AUTHORIZATION [web_user]")]
    [InlineData("CREATE ROLE [owned_role] AUTHORIZATION [web_user]")]
    [InlineData("ALTER ROLE [workbench_migrator] ADD MEMBER [workbench_web]")]
    [InlineData("CREATE SCHEMA [owned_role_schema] AUTHORIZATION [workbench_web]")]
    [InlineData("ALTER AUTHORIZATION ON OBJECT::[Security].[ReadDatabaseReadiness] TO [workbench_operator]")]
    public async Task ExistingAuthorityIsRejected(string unsafeSetup)
    {
        // GIVEN an existing contained user with authority outside its intended role.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        await ExecuteAsync(database, $"CREATE USER [web_user] WITH PASSWORD=N'{inputs.Password}';");
        await ExecuteAsync(database, unsafeSetup);
        var originalProof = await database.GetTenantContextProofKeyAsync();
        // WHEN provisioning is attempted, THEN it rejects the authority without partial provisioning.
        await Assert.ThrowsAnyAsync<Exception>(() => inputs.ProvisionAsync(database));
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_principals WHERE name='operator_user'"));
        Assert.Equal(originalProof, await database.GetTenantContextProofKeyAsync());
    }

    [Theory]
    [InlineData("CREATE ROLE [web_user]")]
    [InlineData("CREATE USER [web_user] WITHOUT LOGIN")]
    public async Task OtherPrincipalTypesAreRejected(string setup)
    {
        // GIVEN a name already belonging to something other than a password-authenticated contained user.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        await ExecuteAsync(database, setup);
        // WHEN provisioning is attempted, THEN the identity is not repurposed.
        await Assert.ThrowsAnyAsync<Exception>(() => inputs.ProvisionAsync(database));
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id=USER_ID('web_user')"));
    }

    [Fact]
    public async Task LateFailureRollsBackUsersRolesAndProof()
    {
        // GIVEN a missing proof row, discovered after the principal writes.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        await ExecuteAsync(database, "DELETE FROM [Security].[TenantContextKeys]");
        // WHEN the final write fails, THEN the entire provisioning transaction rolls back.
        await Assert.ThrowsAnyAsync<Exception>(() => inputs.ProvisionAsync(database));
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_principals WHERE name IN ('web_user','operator_user','migrator_user')"));
    }

    [Fact]
    public async Task DistinctUsersCanBeProvisionedRepeatedlyWithSeparateAuthority()
    {
        // GIVEN a migrated installation and three distinct contained users.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        using var inputs = new Inputs();
        // WHEN provisioning and repeating the same request.
        await inputs.ProvisionAsync(database);
        await inputs.ProvisionAsync(database);
        // THEN role separation and the proof remain intact.
        Assert.Equal(inputs.Proof, await database.GetTenantContextProofKeyAsync());
        Assert.Equal(3, await ScalarAsync(database, "SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id IN (USER_ID('web_user'),USER_ID('operator_user'),USER_ID('migrator_user'))"));
        foreach (var principal in inputs.Principals)
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(database.AdminConnectionString)
            {
                UserID = principal.User,
                Password = inputs.Password,
            }.ConnectionString);
            await connection.OpenAsync();
            await using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL')", connection);
            Assert.Equal(principal.Role == "workbench_migrator" ? 1 : 0, Convert.ToInt32(await permission.ExecuteScalarAsync()));
        }
    }

    private static async Task ExecuteAsync(SqlTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class Inputs : IDisposable
    {
        private readonly string passwordFile = Path.GetTempFileName();
        public string Password { get; } = $"W0rkbench-{Guid.NewGuid():N}!";
        public byte[] Proof { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public PasswordPrincipal[] Principals { get; }

        public Inputs()
        {
            File.WriteAllText(passwordFile, Password);
            Principals = [new("web_user", passwordFile, "workbench_web"), new("operator_user", passwordFile, "workbench_operator"), new("migrator_user", passwordFile, "workbench_migrator")];
        }

        public Task ProvisionAsync(SqlTestDatabase database) => PasswordPrincipalProvisioning.ProvisionAsync(database.AdminConnectionString, Principals, Proof);
        public void Dispose() => File.Delete(passwordFile);
    }
}
