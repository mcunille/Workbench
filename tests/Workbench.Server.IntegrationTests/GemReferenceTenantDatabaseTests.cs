// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceTenantDatabaseTests(SqlServerFixture sqlServer)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task TenantCommandsPersistPrivateContentArchiveAndResetWithoutVersionReuse()
    {
        // GIVEN active tenant authority and the caller's publication-then-tenant locked transaction.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var content = Addition();
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var version = await SaveAsync(web, transaction, content);
        Assert.Equal(8, version.Length);
        // WHEN saving and archiving THEN content/audit persist and every transition advances concurrency.
        var next = await SaveAsync(web, transaction, content with { CommonName = "Private pearl" }, version);
        Assert.NotEqual(version, next);
        var archived = await VersionAsync(web, transaction,
            "EXEC Gemology.SetTenantEntryArchive @ActorId=@actor,@EntryId=@id,@IsArchived=1,@ExpectedTenantRowVersion=@version",
            ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("version", next));
        Assert.NotEqual(next, archived);
        Assert.Equal(true, await ScalarAsync(web, transaction, "SELECT IsArchived FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(AuthTestApplication.MemberUserId, await ScalarAsync(web, transaction, "SELECT UpdatedBy FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        // AND an old archive token cannot restore the entry, while the current token restores the same ID.
        const string restore = "EXEC Gemology.SetTenantEntryArchive @ActorId=@actor,@EntryId=@id,@IsArchived=0,@ExpectedTenantRowVersion=@version";
        Assert.Equal(50054, (await Assert.ThrowsAsync<SqlException>(() => VersionAsync(web, transaction, restore,
            ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("version", next)))).Number);
        Assert.Equal(true, await ScalarAsync(web, transaction, "SELECT IsArchived FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        var restored = await VersionAsync(web, transaction, restore, ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("version", archived));
        Assert.NotEqual(archived, restored);
        Assert.Equal(false, await ScalarAsync(web, transaction, "SELECT IsArchived FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        var sharedVersion = (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", shared.Id));
        var replacement = "{\"description\":{\"state\":\"replace\",\"value\":\"Tenant description\",\"sources\":[]}}";
        var overridden = await OverridesAsync(web, transaction, shared.Id, replacement, sharedVersion);
        // WHEN resetting all fields THEN an empty row remains and a pre-reset token cannot recreate it.
        var reset = await OverridesAsync(web, transaction, shared.Id, "{}", sharedVersion, overridden);
        Assert.NotEqual(overridden, reset);
        Assert.Equal("{}", await ScalarAsync(web, transaction, "SELECT OverridesJson FROM Gemology.TenantOverrides WHERE EntryId=@id", ("id", shared.Id)));
        Assert.Equal(50054, (await Assert.ThrowsAsync<SqlException>(() => OverridesAsync(web, transaction, shared.Id, replacement, sharedVersion, overridden))).Number);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task RlsHidesOtherTenantAndBlocksProofSubstitutionAtStorageBoundary()
    {
        // GIVEN a real row saved by one tenant and another valid tenant proof.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var content = Addition();
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        byte[] version;
        await using (var web = await OpenAsync(app))
        await using (var transaction = await LockAsync(web))
        {
            version = await SaveAsync(web, transaction, content);
            var sharedVersion = (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", shared.Id));
            await OverridesAsync(web, transaction, shared.Id, "{}", sharedVersion);
            await transaction.CommitAsync();
        }
        // AND EF's independent owner-connection filter cannot browse another tenant or tenant-free service-admin data.
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(app.AdminConnectionString).Options;
        foreach (var tenant in new Guid?[] { AuthTestApplication.TenantId, OtherTenant, null })
        {
            await using var db = new WorkbenchDbContext(options, new TenantContext(tenant));
            Assert.Equal(tenant == AuthTestApplication.TenantId ? 1 : 0, await db.GemReferenceTenantEntries.CountAsync());
            Assert.Equal(tenant == AuthTestApplication.TenantId ? 1 : 0, await db.GemReferenceTenantOverrides.CountAsync());
        }
        // AND absent proof and an unproved substituted TenantId reveal no stored tenant data.
        await using (var unproved = new SqlConnection(app.WebConnectionString))
        {
            await unproved.OpenAsync();
            Assert.Equal(0, await ScalarAsync(unproved, null, "SELECT COUNT(*) FROM Gemology.TenantEntries"));
            await ScalarAsync(unproved, null, "EXEC sys.sp_set_session_context @key=N'TenantId',@value=@tenant", ("tenant", AuthTestApplication.TenantId));
            Assert.Equal(0, await ScalarAsync(unproved, null, "SELECT COUNT(*) FROM Gemology.TenantEntries"));
            Assert.Equal(0, await ScalarAsync(unproved, null, "SELECT COUNT(*) FROM Gemology.TenantOverrides"));
        }
        await using var foreign = await OpenAsync(app, OtherTenant);
        await using var foreignTransaction = await LockAsync(foreign, OtherTenant);
        // WHEN reading or updating a known foreign ID THEN it is invisible and the command returns not found.
        Assert.Equal(0, await ScalarAsync(foreign, foreignTransaction, "SELECT COUNT(*) FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(0, await ScalarAsync(foreign, foreignTransaction, "SELECT COUNT(*) FROM Gemology.TenantOverrides"));
        Assert.Equal(50055, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(foreign, foreignTransaction, content, version, AuthTestApplication.OtherTenantUserId))).Number);
        await foreignTransaction.RollbackAsync();
        // AND a probe principal granted table writes still cannot bypass RLS on insertion or tenant substitution.
        await using var owner = new SqlConnection(app.AdminConnectionString);
        await owner.OpenAsync();
        await app.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(owner, AuthTestApplication.TenantId, default);
        await ScalarAsync(owner, null, """
            CREATE USER gem_tenant_rls_probe WITHOUT LOGIN;
            GRANT SELECT,INSERT,UPDATE ON Gemology.TenantEntries TO gem_tenant_rls_probe;
            GRANT SELECT,INSERT,UPDATE ON Gemology.TenantOverrides TO gem_tenant_rls_probe;
            EXECUTE AS USER=N'gem_tenant_rls_probe';
            """);
        await using var insert = Command(owner, null, """
            INSERT Gemology.TenantEntries(TenantId,Id,ContentJson,IsArchived,CreatedBy,UpdatedBy,CreatedAtUtc,UpdatedAtUtc)
            VALUES(@tenant,@id,@json,0,@actor,@actor,SYSUTCDATETIME(),SYSUTCDATETIME())
            """, ("tenant", OtherTenant), ("id", Guid.NewGuid()), ("json", JsonSerializer.Serialize(content, Json)), ("actor", AuthTestApplication.OtherTenantUserId));
        Assert.Equal(33504, (await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync())).Number);
        await using var update = Command(owner, null, """
            UPDATE Gemology.TenantEntries SET TenantId=@tenant,CreatedBy=@actor,UpdatedBy=@actor WHERE Id=@id
            """, ("tenant", OtherTenant), ("id", content.Id), ("actor", AuthTestApplication.OtherTenantUserId));
        Assert.Equal(33504, (await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync())).Number);
        foreach (var statement in new[] {
            "INSERT Gemology.TenantOverrides(TenantId,EntryId,OverridesJson,CreatedBy,UpdatedBy,CreatedAtUtc,UpdatedAtUtc) VALUES(@tenant,@id,N'{}',@actor,@actor,SYSUTCDATETIME(),SYSUTCDATETIME())",
            "UPDATE Gemology.TenantOverrides SET TenantId=@tenant,CreatedBy=@actor,UpdatedBy=@actor WHERE EntryId=@id" })
        {
            await using var command = Command(owner, null, statement, ("tenant", OtherTenant), ("id", shared.Id), ("actor", AuthTestApplication.OtherTenantUserId));
            Assert.Equal(33504, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
    }

    [Theory]
    [InlineData("missingContext")]
    [InlineData("substitutedContext")]
    [InlineData("inactiveActor")]
    [InlineData("inactiveTenant")]
    [InlineData("foreignActor")]
    [InlineData("serviceAdmin")]
    public async Task SqlAuthorityRejectsMissingForgedAndInactiveTenantAuthority(string state)
    {
        // GIVEN a real web user, with the indicated independent authority failure.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var web = new SqlConnection(app.WebConnectionString);
        await web.OpenAsync();
        if (state == "substitutedContext")
            await ScalarAsync(web, null, "EXEC sys.sp_set_session_context @key=N'TenantId',@value=@tenant", ("tenant", AuthTestApplication.TenantId));
        else if (state is not ("missingContext" or "serviceAdmin"))
            await app.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(web, AuthTestApplication.TenantId, default);
        if (state == "serviceAdmin") await app.ProvisionServiceAdminAsync();
        if (state == "inactiveActor")
            await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(app.AdminConnectionString, "UPDATE [Identity].Users SET State=0 WHERE Id=@id", ("id", AuthTestApplication.MemberUserId));
        if (state == "inactiveTenant")
            await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(app.AdminConnectionString, "UPDATE Tenancy.Tenants SET IsEnabled=0 WHERE Id=@id", ("id", AuthTestApplication.TenantId));
        await using var transaction = await LockAsync(web);
        // WHEN bypassing HTTP THEN SQL checks authority before attempting persistence.
        Assert.Equal(50051, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, Addition(), actor: state switch
        { "foreignActor" => AuthTestApplication.OtherTenantUserId, "serviceAdmin" => AuthTestApplication.ServiceAdminId, _ => AuthTestApplication.MemberUserId }))).Number);
    }

    [Theory]
    [InlineData("noTransaction")]
    [InlineData("noLocks")]
    [InlineData("publicationOnly")]
    [InlineData("tenantOnly")]
    public async Task CommandsRequireCallerTransactionAndBothExclusiveLocks(string state)
    {
        // GIVEN valid authority without one required transaction/lock condition.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var web = await OpenAsync(app);
        await using var transaction = state == "noTransaction" ? null : (SqlTransaction)await web.BeginTransactionAsync();
        if (state == "publicationOnly") await TakeLockAsync(web, transaction!, "Gemology.Publication");
        if (state == "tenantOnly") await TakeLockAsync(web, transaction!, "Gemology.Tenant:" + AuthTestApplication.TenantId.ToString("D"));
        // WHEN directly executing THEN the command rejects the absent caller lock contract.
        Assert.Equal(50052, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, Addition()))).Number);
    }

    [Theory]
    [InlineData("workbench_web")]
    [InlineData("workbench_worker")]
    [InlineData("workbench_operator")]
    public async Task ActualPrincipalsHaveOnlyNarrowRuntimeAuthority(string role)
    {
        // GIVEN actual workload users and valid arguments that reach the SQL permissions boundary.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var connectionString = role switch { "workbench_web" => app.WebConnectionString, "workbench_worker" => await app.CreateWorkerConnectionAsync(), _ => await app.CreateOperatorConnectionAsync() };
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        await app.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(sql, AuthTestApplication.TenantId, default);
        await using var transaction = await LockAsync(sql);
        // WHEN mutating tables directly THEN no runtime user can insert, update or physically delete rows.
        foreach (var table in new[] { "TenantEntries", "TenantOverrides" })
        {
            foreach (var statement in new[] { $"INSERT Gemology.{table} DEFAULT VALUES", $"UPDATE Gemology.{table} SET TenantId=TenantId", $"DELETE Gemology.{table}" })
            {
                await using var command = Command(sql, transaction, statement);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
            await using var read = Command(sql, transaction, $"SELECT COUNT(*) FROM Gemology.{table}");
            if (role == "workbench_web") Assert.Equal(0, await read.ExecuteScalarAsync());
            else Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => read.ExecuteScalarAsync())).Number);
        }
        // AND only web can invoke the narrow commands, with valid tenant authority.
        if (role == "workbench_web") Assert.Equal(8, (await SaveAsync(sql, transaction, Addition())).Length);
        else Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(sql, transaction, Addition()))).Number);
        foreach (var commandName in new[] { "SetTenantEntryArchive", "SaveTenantOverrides" })
        {
            await using var command = Command(sql, transaction, commandName == "SetTenantEntryArchive"
                ? "EXEC Gemology.SetTenantEntryArchive @ActorId=@actor,@EntryId=@id,@IsArchived=1,@ExpectedTenantRowVersion=@version"
                : "EXEC Gemology.SaveTenantOverrides @ActorId=@actor,@EntryId=@id,@OverridesJson=N'{}',@ExpectedSharedRowVersion=@version",
                ("actor", AuthTestApplication.MemberUserId), ("id", Guid.NewGuid()), ("version", new byte[8]));
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(role == "workbench_web" ? 50055 : 229, error.Number);
        }
    }

    [Theory]
    [InlineData("arrayRoot")]
    [InlineData("retirementField")]
    [InlineData("clearAliases")]
    [InlineData("inheritState")]
    [InlineData("nullReplacement")]
    [InlineData("numericReplacement")]
    [InlineData("booleanAlias")]
    [InlineData("foreignSourceField")]
    [InlineData("unsourcedLocality")]
    [InlineData("clearWithSources")]
    [InlineData("duplicateField")]
    public async Task OverrideStructuralGuardsRejectMalformedPayloadWithoutPartialWrite(string scenario)
    {
        var payload = scenario switch
        {
            "arrayRoot" => "[]",
            "retirementField" => "{\"isRetired\":true}",
            "clearAliases" => "{\"aliases\":{\"state\":\"clear\",\"sources\":[]}}",
            "inheritState" => "{\"description\":{\"state\":\"inherit\",\"sources\":[]}}",
            "nullReplacement" => "{\"description\":{\"state\":\"replace\",\"value\":null,\"sources\":[]}}",
            "numericReplacement" => "{\"description\":{\"state\":\"replace\",\"value\":123,\"sources\":[]}}",
            "booleanAlias" => "{\"aliases\":{\"state\":\"replace\",\"value\":[true],\"sources\":[]}}",
            "foreignSourceField" => "{\"description\":{\"state\":\"replace\",\"value\":\"text\",\"sources\":[{\"field\":\"commonName\"}]}}",
            "unsourcedLocality" => "{\"notableLocality\":{\"state\":\"replace\",\"value\":{\"place\":\"Hills\",\"scope\":\"claim\",\"reviewedOn\":\"2026-10-01\",\"sourceAssertionId\":\"11111111-1111-1111-1111-111111111111\"},\"sources\":[]}}",
            "clearWithSources" => "{\"description\":{\"state\":\"clear\",\"sources\":[{\"field\":\"description\"}]}}",
            "duplicateField" => "{\"description\":{\"state\":\"replace\",\"value\":\"one\",\"sources\":[]},\"description\":{\"state\":\"replace\",\"value\":\"two\",\"sources\":[]}}",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        // GIVEN a shared entry and valid locked tenant writer.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var version = (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", shared.Id));
        // WHEN malformed sparse data bypasses C# validation THEN SQL rejects it before creating any override.
        Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => OverridesAsync(web, transaction, shared.Id, payload, version))).Number);
        Assert.Equal(0, await ScalarAsync(web, transaction, "SELECT COUNT(*) FROM Gemology.TenantOverrides"));
    }

    [Fact]
    public async Task AdditionStructuralAndVersionGuardsRejectInvalidCommands()
    {
        // GIVEN a valid locked tenant writer and an existing shared ID.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var content = Addition();
        var json = JsonSerializer.Serialize(content, Json);
        // WHEN bypassing validation THEN ID substitution, retirement, bad shape, oversize and bad version are rejected.
        foreach (var payload in new[] { "[]", json.Replace(content.Id.ToString(), Guid.NewGuid().ToString()), json.Replace("\"isRetired\":false", "\"isRetired\":true"), json.Replace("\"aliases\":[]", "\"aliases\":{}"), json.Replace("\"sources\":[]", "\"sources\":[{}]"), new string(' ', 1048577) })
        {
            await using var command = Command(web, transaction, "EXEC Gemology.SaveTenantEntry @ActorId=@actor,@EntryId=@id,@ContentJson=@json",
                ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("json", payload));
            Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, content, [1]))).Number);
        Assert.Equal(50056, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, content with { Id = shared.Id }))).Number);
        var current = await SaveAsync(web, transaction, content);
        Assert.Equal(50054, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, content))).Number);
        Assert.Equal(50054, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, content, new byte[8]))).Number);
        Assert.Equal(current, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        // AND stale shared versions prevent override creation even when tenant version correctly indicates absence.
        Assert.Equal(50054, (await Assert.ThrowsAsync<SqlException>(() => OverridesAsync(web, transaction, shared.Id, "{}", new byte[8]))).Number);
    }

    [Fact]
    public async Task SourceGuardsAcceptIdentifiableLocalityAndRejectOversizedOptionalCitation()
    {
        // GIVEN valid tenant content with both ordinary and locality source assertions.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var source = GemReferenceSamples.Source("notableLocality");
        var content = Addition() with { Sources = [source], NotableLocality = new("Hills", "Documented claim", source.ReviewedOn, source.Id) };
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var version = await SaveAsync(web, transaction, content);
        Assert.Equal(8, version.Length);
        // WHEN an optional citation exceeds its field bound while URL remains valid THEN SQL rejects it atomically.
        var invalid = content with { Sources = [source with { Citation = new string('x', 5000), Url = "https://example.com/source" }] };
        Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => SaveAsync(web, transaction, invalid, version))).Number);
        var malformed = JsonSerializer.Serialize(content, Json).Replace("\"title\":\"" + source.Title + "\"", "\"title\":123", StringComparison.Ordinal);
        Assert.NotEqual(JsonSerializer.Serialize(content, Json), malformed);
        await using var wrongType = Command(web, transaction, "EXEC Gemology.SaveTenantEntry @ActorId=@actor,@EntryId=@id,@ContentJson=@json,@ExpectedTenantRowVersion=@version",
            ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("json", malformed), ("version", version));
        Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => wrongType.ExecuteNonQueryAsync())).Number);
        Assert.Equal(version, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
    }

    [Theory]
    [InlineData("contentKey")]
    [InlineData("overrideKey")]
    [InlineData("overrideProperty")]
    [InlineData("state")]
    [InlineData("materialKind")]
    [InlineData("sourceField")]
    [InlineData("sourceProperty")]
    public async Task ContractTokensRejectTrailingSpacesWithoutChangingStoredChoices(string token)
    {
        // GIVEN saved tenant content and overrides under actual web authority, with nonempty source metadata.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var content = Addition() with { CommonName = " Private gem ", Description = " Tenant description " };
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var tenantVersion = await SaveAsync(web, transaction, content);
        var contentJson = JsonSerializer.Serialize(content, Json);
        var source = GemReferenceSamples.Source("description") with { Url = "https://example.com/source", Citation = "Tenant citation" };
        var choices = new Dictionary<string, GemReferenceFieldOverride>
        {
            ["description"] = new("replace", JsonSerializer.SerializeToElement(" Tenant description "), [source])
        };
        var overridesJson = JsonSerializer.Serialize(choices, Json);
        var sharedVersion = (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", shared.Id));
        var overrideVersion = await OverridesAsync(web, transaction, shared.Id, overridesJson, sharedVersion);
        // WHEN a structural key or enum token has a trailing space THEN SQL rejects it with no content/version change.
        var payload = token switch
        {
            "contentKey" => contentJson.Replace("\"commonName\":", "\"commonName \":", StringComparison.Ordinal),
            "materialKind" => contentJson.Replace("\"materialKind\":\"organic\"", "\"materialKind\":\"organic \"", StringComparison.Ordinal),
            "overrideKey" => overridesJson.Replace("\"description\":", "\"description \":", StringComparison.Ordinal),
            "overrideProperty" => overridesJson.Replace("\"value\":", "\"value \":", StringComparison.Ordinal),
            "state" => overridesJson.Replace("\"state\":\"replace\"", "\"state\":\"replace \"", StringComparison.Ordinal),
            "sourceField" => overridesJson.Replace("\"field\":\"description\"", "\"field\":\"description \"", StringComparison.Ordinal),
            _ => overridesJson.Replace("\"citation\":", "\"citation \":", StringComparison.Ordinal),
        };
        if (token is "contentKey" or "materialKind")
        {
            await using var command = Command(web, transaction, "EXEC Gemology.SaveTenantEntry @ActorId=@actor,@EntryId=@id,@ContentJson=@json,@ExpectedTenantRowVersion=@version",
                ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("json", payload), ("version", tenantVersion));
            Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        else Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => OverridesAsync(web, transaction, shared.Id, payload, sharedVersion, overrideVersion))).Number);
        // AND ordinary field text keeps its accepted whitespace while malformed contract tokens cannot enter storage.
        Assert.Equal(contentJson, await ScalarAsync(web, transaction, "SELECT ContentJson FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(tenantVersion, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(overridesJson, await ScalarAsync(web, transaction, "SELECT OverridesJson FROM Gemology.TenantOverrides WHERE EntryId=@id", ("id", shared.Id)));
        Assert.Equal(overrideVersion, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantOverrides WHERE EntryId=@id", ("id", shared.Id)));
    }

    [Theory]
    [InlineData("entry", "reviewCompact")]
    [InlineData("override", "reviewCompact")]
    [InlineData("entry", "reviewSlash")]
    [InlineData("override", "reviewSlash")]
    [InlineData("entry", "accessTimestamp")]
    [InlineData("override", "accessTimestamp")]
    [InlineData("entry", "localityPadded")]
    [InlineData("override", "localityPadded")]
    public async Task PersistedDateTokensRemainCanonicalAndEffectiveReadsSurviveRejectedWrites(string storage, string scenario)
    {
        // GIVEN stored canonical dates, including null access dates, under actual locked web authority.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var source = GemReferenceSamples.Source("description");
        var localitySource = GemReferenceSamples.Source("notableLocality") with { AccessedOn = null };
        var locality = new GemReferenceLocalityContent("Hills", "Documented claim", localitySource.ReviewedOn, localitySource.Id);
        var content = Addition() with { Description = "Private description", Sources = [source, localitySource], NotableLocality = locality };
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, shared);
        await using var web = await OpenAsync(app);
        await using var transaction = await LockAsync(web);
        var contentJson = JsonSerializer.Serialize(content, Json);
        var tenantVersion = await SaveAsync(web, transaction, content);
        var choices = new Dictionary<string, GemReferenceFieldOverride>
        {
            ["description"] = new("replace", JsonSerializer.SerializeToElement("Tenant description"), [source with { AccessedOn = null }]),
            ["notableLocality"] = new("replace", JsonSerializer.SerializeToElement(locality, Json), [localitySource]),
        };
        var overridesJson = JsonSerializer.Serialize(choices, Json);
        var sharedVersion = (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", shared.Id));
        var overrideVersion = await OverridesAsync(web, transaction, shared.Id, overridesJson, sharedVersion);
        var malformed = JsonNode.Parse(storage == "entry" ? contentJson : overridesJson)!;
        var sourceNode = storage == "entry" ? malformed["sources"]![0]! : malformed["description"]!["sources"]![0]!;
        var localityNode = storage == "entry" ? malformed["notableLocality"]! : malformed["notableLocality"]!["value"]!;
        var canonical = source.ReviewedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        switch (scenario)
        {
            case "reviewCompact": sourceNode["reviewedOn"] = canonical.Replace("-", "", StringComparison.Ordinal); break;
            case "reviewSlash": sourceNode["reviewedOn"] = canonical.Replace("-", "/", StringComparison.Ordinal); break;
            case "accessTimestamp": sourceNode["accessedOn"] = canonical + "T00:00:00"; break;
            case "localityPadded": localityNode["reviewedOn"] = canonical + " "; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        // WHEN SQL-convertible but noncanonical date tokens bypass typed HTTP binding THEN SQL rejects the write.
        if (storage == "entry")
        {
            await using var command = Command(web, transaction, "EXEC Gemology.SaveTenantEntry @ActorId=@actor,@EntryId=@id,@ContentJson=@json,@ExpectedTenantRowVersion=@version",
                ("actor", AuthTestApplication.MemberUserId), ("id", content.Id), ("json", malformed.ToJsonString(Json)), ("version", tenantVersion));
            Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        else Assert.Equal(50053, (await Assert.ThrowsAsync<SqlException>(() => OverridesAsync(web, transaction, shared.Id, malformed.ToJsonString(Json), sharedVersion, overrideVersion))).Number);
        // AND neither saved payload nor its concurrency token changes.
        Assert.Equal(contentJson, await ScalarAsync(web, transaction, "SELECT ContentJson FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(tenantVersion, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantEntries WHERE Id=@id", ("id", content.Id)));
        Assert.Equal(overridesJson, await ScalarAsync(web, transaction, "SELECT OverridesJson FROM Gemology.TenantOverrides WHERE EntryId=@id", ("id", shared.Id)));
        Assert.Equal(overrideVersion, (byte[])await ScalarAsync(web, transaction, "SELECT RowVersion FROM Gemology.TenantOverrides WHERE EntryId=@id", ("id", shared.Id)));
        await transaction.CommitAsync();
        // WHEN the effective reader deserializes the stored snapshot THEN both additions and overrides remain available.
        var tenant = new TenantContext(AuthTestApplication.TenantId);
        await using var database = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(app.WebConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor(tenant, app.Factory.Services.GetRequiredService<TenantContextProof>())).Options, tenant);
        var reads = new GemReferenceEffectiveReadService(database);
        var addition = Assert.IsType<GemReferenceDetailResponse>(await reads.DetailAsync(content.Id, default));
        Assert.Equal(source.ReviewedOn, Assert.Single(addition.SourceAssertions, row => row.Field == "description").ReviewedOn);
        Assert.Equal(source.AccessedOn, Assert.Single(addition.SourceAssertions, row => row.Field == "description").AccessedOn);
        Assert.Equal(locality, addition.NotableLocality);
        var customized = Assert.IsType<GemReferenceDetailResponse>(await reads.DetailAsync(shared.Id, default));
        Assert.Equal(source.ReviewedOn, Assert.Single(customized.SourceAssertions, row => row.Field == "description").ReviewedOn);
        Assert.Null(Assert.Single(customized.SourceAssertions, row => row.Field == "description").AccessedOn);
        Assert.Equal(locality, customized.NotableLocality);
    }

    private static GemReferenceContent Addition() => new(Guid.NewGuid(), "organic", "Private gem", null, null, null, null, [], [], null, false, null, null);
    private static async Task<SqlConnection> OpenAsync(AuthTestApplication app, Guid? tenant = null)
    {
        var sql = new SqlConnection(app.WebConnectionString);
        await sql.OpenAsync();
        await app.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(sql, tenant ?? AuthTestApplication.TenantId, default);
        return sql;
    }
    internal static async Task<SqlTransaction> LockAsync(SqlConnection sql, Guid? tenant = null)
    {
        var transaction = (SqlTransaction)await sql.BeginTransactionAsync();
        await TakeLockAsync(sql, transaction, "Gemology.Publication");
        await TakeLockAsync(sql, transaction, "Gemology.Tenant:" + (tenant ?? AuthTestApplication.TenantId).ToString("D"));
        return transaction;
    }
    private static async Task TakeLockAsync(SqlConnection sql, SqlTransaction transaction, string resource)
    {
        await using var command = Command(sql, transaction, "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=1000; IF @result<0 THROW 51000,'Test lock unavailable.',1;", ("resource", resource));
        await command.ExecuteNonQueryAsync();
    }
    private static Task<byte[]> SaveAsync(SqlConnection sql, SqlTransaction? transaction, GemReferenceContent content, byte[]? version = null, Guid? actor = null)
        => VersionAsync(sql, transaction, "EXEC Gemology.SaveTenantEntry @ActorId=@actor,@EntryId=@id,@ContentJson=@json,@ExpectedTenantRowVersion=@version",
            ("actor", actor ?? AuthTestApplication.MemberUserId), ("id", content.Id), ("json", JsonSerializer.Serialize(content, Json)), ("version", version));
    private static Task<byte[]> OverridesAsync(SqlConnection sql, SqlTransaction transaction, Guid id, string payload, byte[] sharedVersion, byte[]? version = null)
        => VersionAsync(sql, transaction, "EXEC Gemology.SaveTenantOverrides @ActorId=@actor,@EntryId=@id,@OverridesJson=@json,@ExpectedSharedRowVersion=@shared,@ExpectedTenantRowVersion=@version",
            ("actor", AuthTestApplication.MemberUserId), ("id", id), ("json", payload), ("shared", sharedVersion), ("version", version));
    private static async Task<byte[]> VersionAsync(SqlConnection sql, SqlTransaction? transaction, string statement, params (string Name, object? Value)[] args)
    {
        await using var command = Command(sql, transaction, statement, args);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (byte[])reader["RowVersion"];
    }
    private static async Task<object> ScalarAsync(SqlConnection sql, SqlTransaction? transaction, string statement, params (string Name, object? Value)[] args)
    {
        await using var command = Command(sql, transaction, statement, args);
        return (await command.ExecuteScalarAsync())!;
    }
    private static SqlCommand Command(SqlConnection sql, SqlTransaction? transaction, string statement, params (string Name, object? Value)[] args)
    {
        var command = new SqlCommand(statement, sql, transaction);
        foreach (var (name, value) in args)
        {
            if (name is "version" or "shared")
                command.Parameters.Add(new SqlParameter("@" + name, SqlDbType.VarBinary, -1) { Value = value ?? DBNull.Value });
            else command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        }
        return command;
    }
}
