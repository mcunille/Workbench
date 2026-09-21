// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Workbench.Server.Tenancy;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedAccountingCommandRejectsMissingActorAndDirectWrites()
    {
        // GIVEN the migrated schema under the actual restricted web principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        // WHEN bypassing the accounting command or supplying no authenticated actor.
        await using var save = new SqlCommand("EXEC [Accounting].[Save] @ActorId=NULL,@SessionId=NULL,@RequestId=@request,@Operation=N'CreateAccounts',@Payload=N'[]'", connection);
        save.Parameters.AddWithValue("@request", Guid.NewGuid());
        // THEN authorization fails and direct table mutations are prohibited.
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() => save.ExecuteNonQueryAsync())).Number);
        foreach (var table in new[] { "Accounts", "Configurations", "Revisions", "Receipts" })
        {
            await using var command = new SqlCommand($"DELETE Accounting.{table}", connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
    }

    [Fact]
    public async Task AtomicChartReplayVersionsAndArchivePreserveEvidence()
    {
        // GIVEN a freshly authorized accounting administrator and an empty chart.
        await using var context = await OpenAsync();
        var request = Guid.NewGuid();
        const string accounts = """[{"code":"1000","name":"Cash","type":"Asset","purpose":"Cash","description":null},{"code":"2000","name":"Payables","type":"Liability","purpose":"SupplierPayable","description":null}]""";
        // WHEN the starter chart request is retried and its payload is then changed.
        var first = await SaveAsync(context, request, "CreateAccounts", accounts);
        var replay = await SaveAsync(context, request, "CreateAccounts", accounts);
        Assert.Equal(first, replay);
        Assert.Equal(50909, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, request, "CreateAccounts", accounts.Replace("Cash", "Bank")))).Number);
        // THEN exactly the original two accounts and two revisions exist.
        Assert.Equal(2, await CountAsync(context, "Accounts"));
        Assert.Equal(2, await CountAsync(context, "Revisions"));
        Assert.Equal(1, await CountAsync(context, "Receipts"));
        var id = JsonSerializer.Deserialize<Guid[]>(first.Ids)![0];
        var updated = await SaveAsync(context, Guid.NewGuid(), "UpdateAccount", """{"code":"1000","name":"Till","description":"Renamed"}""", id, first.Version);
        Assert.Equal(50909, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""", id, first.Version))).Number);
        var archived = await SaveAsync(context, Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""", id, updated.Version);
        Assert.Equal(50909, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "CreateAccounts", """[{"code":"1000","name":"Duplicate","type":"Asset","purpose":"General"}]"""))).Number);
        await SaveAsync(context, Guid.NewGuid(), "ArchiveAccount", """{"isArchived":false}""", id, archived.Version);
        Assert.Equal(2, await CountAsync(context, "Accounts"));
        Assert.Equal(5, await CountAsync(context, "Revisions"));
    }

    [Fact]
    public async Task ConfigurationEligibilityAndMappingArchiveRaceAreSerialized()
    {
        // GIVEN a supplier payable account and two independent connections sharing tenant proof.
        await using var context = await OpenAsync();
        var created = await SaveAsync(context, Guid.NewGuid(), "CreateAccounts", """[{"code":"2000","name":"Payables","type":"Liability","purpose":"SupplierPayable"}]""");
        var id = JsonSerializer.Deserialize<Guid[]>(created.Ids)![0];
        string Configuration(string slot, Guid target) => JsonSerializer.Serialize(new { policies = new { }, mappings = new[] { new { slot, accountId = target } }, coverage = Array.Empty<object>() });
        // WHEN wrong-purpose and unavailable accounts are submitted directly to SQL.
        Assert.Equal(50900, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "Configure", Configuration("SupplierAdvance", id), version: Guid.Empty))).Number);
        Assert.Equal(50900, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "Configure", Configuration("SupplierPayable", Guid.NewGuid()), version: Guid.Empty))).Number);
        Assert.Equal(0, await CountAsync(context, "Configurations"));
        // THEN concurrent mapping and archive cannot both succeed.
        await using var second = await OpenSiblingAsync(context);
        async Task<bool> Attempt(Func<Task<(Guid Version, string Ids)>> operation)
        {
            try { await operation(); return true; }
            catch (SqlException exception) when (exception.Number is 50900 or 50909) { return false; }
        }
        var results = await Task.WhenAll(
            Attempt(() => SaveAsync(context, Guid.NewGuid(), "Configure", Configuration("SupplierPayable", id), version: Guid.Empty)),
            Attempt(() => SaveAsync(second, Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""", id, created.Version)));
        Assert.Single(results, result => result);
        Assert.Equal(2, await CountAsync(context, "Receipts"));
    }

    [Theory]
    [InlineData("{\"policies\":{\"scale\":5},\"mappings\":[],\"coverage\":[]}")]
    [InlineData("{\"policies\":{\"plannedStartDate\":\"2026-02-30\"},\"mappings\":[],\"coverage\":[]}")]
    [InlineData("{\"policies\":{\"country\":\"US\",\"region\":\"BC\"},\"mappings\":[],\"coverage\":[]}")]
    [InlineData("{\"policies\":{},\"mappings\":[],\"coverage\":[],\"bookkeepingAvailable\":true}")]
    [InlineData("{\"policies\":{\"Country\":\"US\"},\"mappings\":[],\"coverage\":[]}")]
    [InlineData("{\"policies\":{},\"mappings\":[\"invalid\"],\"coverage\":[]}")]
    [InlineData("{\"policies\":{},\"mappings\":[],\"coverage\":[null]}")]
    public async Task DirectConfigurationRejectsMalformedPoliciesAndActivationFlags(string payload)
    {
        // GIVEN a valid current accounting administrator.
        await using var context = await OpenAsync();
        // WHEN bypassing HTTP validation with malformed accounting configuration.
        var exception = await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "Configure", payload, version: Guid.Empty));
        // THEN SQL rejects the input with no configuration, revision or receipt.
        Assert.Equal(50900, exception.Number);
        Assert.Equal(0, await CountAsync(context, "Configurations"));
        Assert.Equal(0, await CountAsync(context, "Revisions"));
        Assert.Equal(0, await CountAsync(context, "Receipts"));
    }

    [Fact]
    public async Task RevocationPreventsReceiptReplayAndOtherTenantRowsStayHidden()
    {
        // GIVEN an existing successful accounting command and a foreign tenant account.
        await using var context = await OpenAsync();
        var request = Guid.NewGuid();
        const string payload = """[{"code":"1000","name":"Bank","type":"Asset","purpose":"Bank"}]""";
        await SaveAsync(context, request, "CreateAccounts", payload);
        await using var admin = new SqlConnection(context.Application.AdminConnectionString);
        await admin.OpenAsync();
        var foreignId = Guid.NewGuid();
        await using var foreign = new SqlCommand("""
            INSERT Accounting.Accounts(Id,TenantId,Code,Name,Type,Purpose,Version)
            VALUES(@id,'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','2000','Foreign payable','Liability','SupplierPayable',NEWID());
            """, admin);
        foreign.Parameters.AddWithValue("@id", foreignId);
        await foreign.ExecuteNonQueryAsync();
        var config = JsonSerializer.Serialize(new { policies = new { }, mappings = new[] { new { slot = "SupplierPayable", accountId = foreignId } }, coverage = Array.Empty<object>() });
        // WHEN selecting a foreign account, RLS and command eligibility hide it.
        Assert.Equal(1, await CountAsync(context, "Accounts"));
        Assert.Equal(50900, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "Configure", config, version: Guid.Empty))).Number);
        await using var revoke = new SqlCommand("DELETE ur FROM [Identity].[UserRoles] ur JOIN Administration.AccountingRoles ar ON ar.TenantId=ur.TenantId AND ar.RoleId=ur.RoleId WHERE ur.UserId=@actor", admin);
        revoke.Parameters.AddWithValue("@actor", AuthTestApplication.AdminUserId);
        await revoke.ExecuteNonQueryAsync();
        // THEN already-admitted requests and replay receipts still require current authority.
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, request, "CreateAccounts", payload))).Number);
    }
    [Fact]
    public async Task CompetingConfigurationVersionsHaveOneWinner()
    {
        // GIVEN two independently admitted requests with the same initial version.
        await using var first = await OpenAsync();
        await using var second = await OpenSiblingAsync(first);
        async Task<bool> Attempt(Context context, int month)
        {
            try
            {
                await SaveAsync(context, Guid.NewGuid(), "Configure", JsonSerializer.Serialize(new { policies = new { fiscalStartMonth = month }, mappings = Array.Empty<object>(), coverage = Array.Empty<object>() }), version: Guid.Empty);
                return true;
            }
            catch (SqlException exception) when (exception.Number == 50909) { return false; }
        }
        // WHEN both configurations race on real SQL connections.
        var results = await Task.WhenAll(Attempt(first, 1), Attempt(second, 7));
        // THEN one commit survives and the stale request leaves no evidence or receipt.
        Assert.Single(results, result => result);
        Assert.Equal(1, await CountAsync(first, "Configurations"));
        Assert.Equal(1, await CountAsync(first, "Revisions"));
        Assert.Equal(1, await CountAsync(first, "Receipts"));
    }
    [Fact]
    public async Task DirectSqlRejectsPaddedJsonKeys()
    {
        // GIVEN a current administrator bypassing the HTTP JSON contract.
        await using var context = await OpenAsync();
        // WHEN a policy key contains a trailing space that SQL comparisons normally ignore.
        var exception = await Assert.ThrowsAsync<SqlException>(() => SaveAsync(context, Guid.NewGuid(), "Configure", """{"policies":{"country ":"XX"},"mappings":[],"coverage":[]}""", version: Guid.Empty));
        // THEN the exact JSON shape is rejected rather than saving an unreadable configuration.
        Assert.Equal(50900, exception.Number);
        Assert.Equal(0, await CountAsync(context, "Configurations"));
    }
    private sealed class Context(AuthTestApplication application, SqlConnection connection, Guid session, byte[] proof, bool ownsApplication) : IAsyncDisposable
    {
        public AuthTestApplication Application { get; } = application;
        public SqlConnection Connection { get; } = connection;
        public Guid Session { get; } = session;
        public byte[] Proof { get; } = proof;
        public async ValueTask DisposeAsync() { await Connection.DisposeAsync(); if (ownsApplication) await Application.DisposeAsync(); }
    }
    private async Task<Context> OpenAsync()
    {
        var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var admin = new SqlConnection(application.AdminConnectionString);
        await admin.OpenAsync();
        var session = Guid.NewGuid();
        await using var seed = new SqlCommand("""
            INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].[Sessions](Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].[Users] WHERE Id=@actor;
            SELECT ProofKey FROM Security.TenantContextKeys WHERE Id=1;
            """, admin);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        seed.Parameters.AddWithValue("@actor", AuthTestApplication.AdminUserId);
        seed.Parameters.AddWithValue("@session", session);
        var proof = (byte[])(await seed.ExecuteScalarAsync())!;
        var connection = new SqlConnection(application.WebConnectionString);
        await connection.OpenAsync();
        await new TenantContextProof(proof).ApplyAsync(connection, AuthTestApplication.TenantId, CancellationToken.None);
        return new(application, connection, session, proof, true);
    }
    private static async Task<Context> OpenSiblingAsync(Context original)
    {
        var connection = new SqlConnection(original.Application.WebConnectionString);
        await connection.OpenAsync();
        await new TenantContextProof(original.Proof).ApplyAsync(connection, AuthTestApplication.TenantId, CancellationToken.None);
        return new(original.Application, connection, original.Session, original.Proof, false);
    }
    private static async Task<(Guid Version, string Ids)> SaveAsync(Context context, Guid request, string operation, string payload, Guid? id = null, Guid? version = null)
    {
        await using var command = new SqlCommand("EXEC [Accounting].[Save] @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Operation=@operation,@Id=@id,@ExpectedVersion=@version,@Payload=@payload", context.Connection);
        command.Parameters.AddWithValue("@actor", AuthTestApplication.AdminUserId);
        command.Parameters.AddWithValue("@session", context.Session);
        command.Parameters.AddWithValue("@request", request);
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", (object?)version ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", payload);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(0), reader.GetString(1));
    }
    private static async Task<int> CountAsync(Context context, string table)
    {
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM Accounting.{table}", context.Connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
