// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceTenantWriteTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _app = null!;
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public async Task InitializeAsync() => _app = await AuthTestApplication.CreateAsync(sqlServer);
    public async Task DisposeAsync() => await _app.DisposeAsync();
    private WorkbenchDbContext Database(Guid? tenant = null)
    {
        var context = new TenantContext(tenant ?? AuthTestApplication.TenantId);
        return new(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(_app.WebConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor(context, _app.Factory.Services.GetRequiredService<TenantContextProof>())).Options, context);
    }
    private static GemReferenceContent Addition(string name = "Private pearl") => new(Guid.NewGuid(), "organic", name,
        null, null, null, null, [], [], null, false, null, null);
    private static GemReferenceFieldOverride Replace<T>(T value, params GemReferenceSourceContent[] sources) =>
        new("replace", JsonSerializer.SerializeToElement(value), sources);
    private static GemReferenceFieldOverride Clear() => new("clear", null, []);
    private static GemReferenceDetailResponse Saved(GemReferenceTenantWriteResult result)
    {
        Assert.True(result.Success, $"{result.Code}: {JsonSerializer.Serialize(result.Errors)}");
        return Assert.IsType<GemReferenceDetailResponse>(result.Detail);
    }
    private async Task<GemReferenceDetailResponse> ReadAsync(Guid id, Guid? tenant = null)
    {
        await using var db = Database(tenant);
        return Assert.IsType<GemReferenceDetailResponse>(await new GemReferenceEffectiveReadService(db).DetailAsync(id, default));
    }
    private async Task<GemReferenceTenantWriteResult> CreateAsync(GemReferenceContent content, Guid? tenant = null)
    {
        await using var db = Database(tenant);
        return await new GemReferenceTenantService(db).CreateAsync(tenant is null ? AuthTestApplication.MemberUserId : AuthTestApplication.OtherTenantUserId, content, default);
    }
    private async Task<GemReferenceTenantWriteResult> OverrideAsync(Guid id, IReadOnlyDictionary<string, GemReferenceFieldOverride> choices, GemReferenceEffectiveVersion version)
    {
        await using var db = Database();
        return await new GemReferenceTenantService(db).SaveOverridesAsync(AuthTestApplication.MemberUserId, id, choices, version, default);
    }
    private async Task<GemReferenceTenantWriteResult> ResetAsync(Guid id, string? field, GemReferenceEffectiveVersion version)
    {
        await using var db = Database();
        return await new GemReferenceTenantService(db).ResetAsync(AuthTestApplication.MemberUserId, id, field, version, default);
    }
    private async Task<GemReferenceTenantWriteResult> ArchiveAsync(Guid id, bool archived, GemReferenceEffectiveVersion version)
    {
        await using var db = Database();
        return await new GemReferenceTenantService(db).SetArchivedAsync(AuthTestApplication.MemberUserId, id, archived, version, default);
    }
    private async Task PublishAsync(GemReferenceContent content)
    {
        await _app.ProvisionServiceAdminAsync();
        var session = await GemReferenceDraftTests.CreateSessionAsync(_app);
        var current = await new GemReferenceAdminReadService(_app.WebConnectionString).DetailAsync(content.Id, default);
        var draft = await new GemReferenceDraftService(_app.WebConnectionString).SaveAsync(AuthTestApplication.ServiceAdminId, session,
            Guid.NewGuid(), new(content.Id, content, null, current?.RowVersion), default);
        var published = await new GemReferencePublicationService(_app.WebConnectionString).PublishAsync(AuthTestApplication.ServiceAdminId, session,
            new(Guid.NewGuid(), [new(draft.Id, draft.RowVersion)]), default);
        Assert.Equal("published", published.Code);
    }

    [Fact]
    public async Task AdditionLifecycleIsPrivateAndSourcesRemainAtomic()
    {
        // GIVEN a tenant's sourced addition and an independent second tenant.
        var content = Addition() with { Description = "Initial", Sources = [GemReferenceSamples.Source("description")] };
        var created = Saved(await CreateAsync(content));
        await using var db = Database();
        var service = new GemReferenceTenantService(db);
        // WHEN changing content, archiving, and restoring THEN the same ID advances version and remains private.
        var updated = Saved(await service.UpdateAsync(AuthTestApplication.MemberUserId, content.Id,
            content with { Description = null, Sources = [] }, created.EffectiveVersion!, default));
        Assert.Empty(updated.SourceAssertions);
        var archived = Saved(await service.SetArchivedAsync(AuthTestApplication.MemberUserId, content.Id, true, updated.EffectiveVersion!, default));
        Assert.True(archived.IsArchived);
        var restored = Saved(await service.SetArchivedAsync(AuthTestApplication.MemberUserId, content.Id, false, archived.EffectiveVersion!, default));
        Assert.False(restored.IsArchived);
        Assert.NotEqual(created.EffectiveVersion, restored.EffectiveVersion);
        await using var other = Database(OtherTenant);
        Assert.Null(await new GemReferenceEffectiveReadService(other).DetailAsync(content.Id, default));
        var inaccessible = await new GemReferenceTenantService(other).UpdateAsync(AuthTestApplication.OtherTenantUserId,
            content.Id, content, restored.EffectiveVersion!, default);
        Assert.Equal(404, inaccessible.StatusCode);
        Assert.Null(inaccessible.Current);
        Assert.Null((await new GemReferenceTenantService(other).SetArchivedAsync(AuthTestApplication.OtherTenantUserId,
            content.Id, true, restored.EffectiveVersion!, default)).Current);
        // AND tenant-qualified IDs can be reused privately without cross-tenant identity collisions.
        var otherEntry = Saved(await new GemReferenceTenantService(other).CreateAsync(AuthTestApplication.OtherTenantUserId,
            content with { CommonName = "Other tenant pearl" }, default));
        Assert.Equal("Other tenant pearl", otherEntry.CommonName);
        Assert.Equal("Private pearl", (await ReadAsync(content.Id)).CommonName);
    }

    [Fact]
    public async Task ResetReceivesCurrentSharedValuesAndSourcesAndPreservesOtherTenant()
    {
        // GIVEN two replaced fields with exact-field tenant sources.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var current = await ReadAsync(shared.Id);
        var choices = new Dictionary<string, GemReferenceFieldOverride>
        {
            ["commonName"] = Replace("Tenant ruby", GemReferenceSamples.Source("commonName")),
            ["description"] = Replace("Tenant note", GemReferenceSamples.Source("description")),
        };
        Saved(await OverrideAsync(shared.Id, choices, current.EffectiveVersion!));
        var source = GemReferenceSamples.Source("commonName") with { Citation = "Current shared citation", Url = null };
        var correction = shared with { CommonName = "Corrected ruby", Sources = shared.Sources.Where(s => s.Field != "commonName").Append(source).ToArray() };
        await PublishAsync(correction);
        // WHEN resetting one field THEN its current shared value/citation returns and the other choice remains.
        var customized = await ReadAsync(shared.Id);
        var reset = Saved(await ResetAsync(shared.Id, "commonName", customized.EffectiveVersion!));
        Assert.Equal("Corrected ruby", reset.CommonName);
        Assert.Equal("Tenant note", reset.Description);
        Assert.Equal(source.Id, Assert.Single(reset.EffectiveFields["commonName"].Sources).Id);
        Assert.Equal("Current shared citation", Assert.Single(reset.EffectiveFields["commonName"].Sources).Citation);
        Assert.Equal(source.ReviewedOn, Assert.Single(reset.EffectiveFields["commonName"].Sources).ReviewedOn);
        Assert.Equal("workbench", reset.EffectiveFields["commonName"].Attribution);
        Assert.Equal("Corrected ruby", (await ReadAsync(shared.Id, OtherTenant)).CommonName);
        Assert.Empty((await ReadAsync(shared.Id, OtherTenant)).Overrides);
        // AND whole reset removes every deliberate choice while retaining a concurrency token.
        var whole = Saved(await ResetAsync(shared.Id, null, reset.EffectiveVersion!));
        Assert.Empty(whole.Overrides);
        Assert.Equal("workbenchReference", whole.Layer);
        Assert.NotNull(whole.EffectiveVersion!.TenantRowVersion);
    }

    [Fact]
    public async Task ConcurrentSavesHaveOneWinner()
    {
        // GIVEN two edits based on one addition version and separate request connections.
        var content = Addition();
        var created = Saved(await CreateAsync(content));
        async Task<GemReferenceTenantWriteResult> Save(string name)
        {
            await using var db = Database();
            return await new GemReferenceTenantService(db).UpdateAsync(AuthTestApplication.MemberUserId, content.Id,
                content with { CommonName = name, Description = name, Sources = [GemReferenceSamples.Source("description")] }, created.EffectiveVersion!, default);
        }
        // WHEN racing THEN one save commits all fields and sources and the loser sees that accessible version.
        var results = await Task.WhenAll(Save("Winner A"), Save("Winner B"));
        var winner = Saved(Assert.Single(results, r => r.Success));
        var loser = Assert.Single(results, r => !r.Success);
        Assert.Equal("stale_entry", loser.Code);
        Assert.Equal(winner.EffectiveVersion, loser.Current!.EffectiveVersion);
        var stored = await ReadAsync(content.Id);
        Assert.Equal(winner.CommonName, stored.Description);
        Assert.Equal(Assert.Single(winner.SourceAssertions).Id, Assert.Single(stored.SourceAssertions).Id);
    }

    [Fact]
    public async Task AdditionRejectsUnexpectedSharedVersionComponent()
    {
        // GIVEN an addition with no shared component and its current tenant version.
        var content = Addition();
        var created = Saved(await CreateAsync(content));
        var mismatched = created.EffectiveVersion! with { SharedRowVersion = Convert.ToBase64String(new byte[8]) };
        await using var db = Database();
        // WHEN an update supplies a shared version where absence is required THEN it conflicts without saving.
        var result = await new GemReferenceTenantService(db).UpdateAsync(AuthTestApplication.MemberUserId, content.Id,
            content with { CommonName = "Must not save" }, mismatched, default);
        Assert.Equal("stale_entry", result.Code);
        Assert.Equal(created.EffectiveVersion, result.Current!.EffectiveVersion);
        var retained = await ReadAsync(content.Id);
        Assert.Equal(content.CommonName, retained.CommonName);
        Assert.Equal(created.EffectiveVersion, retained.EffectiveVersion);
    }

    [Fact]
    public async Task PublicationBeforeSaveRejectsCompositeVersion()
    {
        // GIVEN a tenant token captured before an actual locked shared publication.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var original = await ReadAsync(shared.Id);
        await PublishAsync(shared with { Description = "Published note", Sources = [.. shared.Sources, GemReferenceSamples.Source("description")] });
        // WHEN saving with the old shared component THEN no override row is created and current shared evidence is returned.
        var result = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride> { ["description"] = Replace("Lost note") }, original.EffectiveVersion!);
        Assert.Equal("stale_entry", result.Code);
        Assert.Equal("Published note", result.Current!.Description);
        Assert.Null(result.Current.EffectiveVersion!.TenantRowVersion);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }

    [Fact]
    public async Task SaveWaitsForPublicationLockAndRecomputesAfterCommit()
    {
        // GIVEN a current version and another transaction holding the real publication lock.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var original = await ReadAsync(shared.Id);
        await using var publication = new SqlConnection(_app.AdminConnectionString);
        await publication.OpenAsync();
        await using var transaction = await GemReferenceTenantDatabaseTests.LockAsync(publication);
        await using var update = new SqlCommand("UPDATE Gemology.Entries SET Description=N'Locked correction' WHERE Id=@id", publication, transaction);
        update.Parameters.AddWithValue("@id", shared.Id);
        await update.ExecuteNonQueryAsync();
        // WHEN a save waits across publication commit THEN it rejects the old token using the newly published snapshot.
        var pending = OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride> { ["description"] = Replace("Tenant note") }, original.EffectiveVersion!);
        var first = await Task.WhenAny(pending, Task.Delay(200));
        Assert.NotSame(pending, first);
        await transaction.CommitAsync();
        var result = await pending;
        Assert.Equal("stale_entry", result.Code);
        Assert.Equal("Locked correction", result.Current!.Description);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }

    [Fact]
    public async Task ResetDoesNotPermitOldOverrideToken()
    {
        // GIVEN an override token before a whole reset.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var initial = await ReadAsync(shared.Id);
        var choices = new Dictionary<string, GemReferenceFieldOverride> { ["description"] = Replace("Tenant note") };
        var customized = Saved(await OverrideAsync(shared.Id, choices, initial.EffectiveVersion!));
        var reset = Saved(await ResetAsync(shared.Id, null, customized.EffectiveVersion!));
        // WHEN attempting recreation with either pre-row or pre-reset version THEN both reject and reset remains intact.
        Assert.Equal("stale_entry", (await OverrideAsync(shared.Id, choices, initial.EffectiveVersion!)).Code);
        Assert.Equal("stale_entry", (await OverrideAsync(shared.Id, choices, customized.EffectiveVersion!)).Code);
        Assert.Equal(reset.EffectiveVersion, (await ReadAsync(shared.Id)).EffectiveVersion);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }

    [Fact]
    public async Task EffectiveIdentityCollisionRejectsAtomically()
    {
        // GIVEN a shared identity and an otherwise different tenant addition.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var content = shared with { Id = Guid.NewGuid(), CommonName = "Private ruby", Sources = [] };
        var created = Saved(await CreateAsync(content));
        await using var db = Database();
        // WHEN an edit becomes Unicode/whitespace equivalent THEN the whole content/source candidate is rejected.
        var result = await new GemReferenceTenantService(db).UpdateAsync(AuthTestApplication.MemberUserId, content.Id,
            content with { CommonName = " Ｒｕｂｙ ", Description = "Must not save", Sources = [GemReferenceSamples.Source("description")] }, created.EffectiveVersion!, default);
        Assert.Equal("duplicate_identity", result.Code);
        var retained = await ReadAsync(content.Id);
        Assert.Equal("Private ruby", retained.CommonName);
        Assert.Null(retained.Description);
        Assert.Empty(retained.SourceAssertions);
        Assert.Equal(created.EffectiveVersion, retained.EffectiveVersion);
        // AND a shared override causing the same collision retains every prior choice.
        var baseline = await ReadAsync(shared.Id);
        var failed = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>
        { ["commonName"] = Replace("Private ruby"), ["description"] = Replace("Must not save") }, baseline.EffectiveVersion!);
        Assert.Equal("duplicate_identity", failed.Code);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }

    [Fact]
    public async Task RestoreRejectsNewVisibleCollision()
    {
        // GIVEN an archived addition whose identity has since become visible elsewhere.
        var content = Addition();
        var original = Saved(await CreateAsync(content));
        var archived = Saved(await ArchiveAsync(content.Id, true, original.EffectiveVersion!));
        Saved(await CreateAsync(content with { Id = Guid.NewGuid() }));
        // WHEN restoring THEN no archive/version mutation occurs.
        var result = await ArchiveAsync(content.Id, false, archived.EffectiveVersion!);
        Assert.Equal("duplicate_identity", result.Code);
        var retained = await ReadAsync(content.Id);
        Assert.True(retained.IsArchived);
        Assert.Equal(archived.EffectiveVersion, retained.EffectiveVersion);
    }

    [Fact]
    public async Task LateSharedCollisionFlagsBothIdsAndArchiveResolvesIt()
    {
        // GIVEN distinct shared and tenant identities before publication.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var addition = shared with { Id = Guid.NewGuid(), CommonName = "Later identity", Sources = [] };
        Saved(await CreateAsync(addition));
        Saved(await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>
        { ["description"] = Replace("Retained choice") }, (await ReadAsync(shared.Id)).EffectiveVersion!));
        await PublishAsync(shared with { CommonName = addition.CommonName });
        // WHEN reading THEN both stable IDs carry review flags, and unrelated edits still conflict.
        var tenant = await ReadAsync(addition.Id);
        var published = await ReadAsync(shared.Id);
        Assert.Contains("identity", tenant.ReviewReasons.Keys);
        Assert.Contains("identity", published.ReviewReasons.Keys);
        Assert.Equal("duplicate_identity", (await CreateAsync(addition with { Id = Guid.NewGuid() })).Code);
        var blocked = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride> { ["description"] = Replace("Unrelated") }, published.EffectiveVersion!);
        Assert.Equal(409, blocked.StatusCode);
        Assert.Equal("Retained choice", (await ReadAsync(shared.Id)).Description);
        // AND a still-conflicting whole reset must also preserve the original choice and version.
        Assert.Equal("duplicate_identity", (await ResetAsync(shared.Id, null, published.EffectiveVersion!)).Code);
        Assert.Equal(published.EffectiveVersion, (await ReadAsync(shared.Id)).EffectiveVersion);
        Assert.Equal("Retained choice", (await ReadAsync(shared.Id)).Description);
        // AND archive may resolve existing collision without deleting content or its ID.
        Saved(await ArchiveAsync(addition.Id, true, tenant.EffectiveVersion!));
        Assert.False((await ReadAsync(shared.Id)).NeedsReview);
        Assert.Equal("Later identity", (await ReadAsync(addition.Id)).CommonName);
    }

    [Fact]
    public async Task LateSharedIdOverlapPreservesBothOriginsAndScopesEveryWrite()
    {
        // GIVEN a private stable ID that a later shared publication reuses for a different identity.
        var addition = Addition();
        var original = Saved(await CreateAsync(addition));
        await PublishAsync(GemReferenceSamples.Mineral() with { Id = addition.Id });
        await using var db = Database();
        var reads = new GemReferenceEffectiveReadService(db);
        var writes = new GemReferenceTenantService(db);
        // WHEN explicitly addressing either origin THEN both remain accessible and the default preserves the private address.
        var tenant = Assert.IsType<GemReferenceDetailResponse>(await reads.DetailAsync(addition.Id, "tenant", default));
        var shared = Assert.IsType<GemReferenceDetailResponse>(await reads.DetailAsync(addition.Id, "workbench", default));
        Assert.Equal("tenant", tenant.Origin);
        Assert.Equal("workbench", shared.Origin);
        Assert.Equal("Private pearl", tenant.CommonName);
        Assert.Equal("Ruby", shared.CommonName);
        Assert.Equal("tenant", (await reads.DetailAsync(addition.Id, default))!.Origin);
        Assert.False(tenant.NeedsReview);
        Assert.False(shared.NeedsReview);
        // AND tenant update and archive affect only its qualified origin while shared overrides and reset remain independent.
        var updated = Saved(await writes.UpdateAsync(AuthTestApplication.MemberUserId, addition.Id,
            addition with { Description = "Private correction" }, original.EffectiveVersion!, default));
        // AND a same-GUID shared counterpart still participates in semantic duplicate validation.
        var duplicate = await writes.UpdateAsync(AuthTestApplication.MemberUserId, addition.Id,
            GemReferenceSamples.Mineral() with { Id = addition.Id, Sources = [] }, updated.EffectiveVersion!, default);
        Assert.Equal("duplicate_identity", duplicate.Code);
        Assert.Equal(updated.EffectiveVersion, duplicate.Current!.EffectiveVersion);
        var customized = Saved(await writes.SaveOverridesAsync(AuthTestApplication.MemberUserId, addition.Id,
            new Dictionary<string, GemReferenceFieldOverride> { ["description"] = Replace("Shared customization") }, shared.EffectiveVersion!, default));
        Assert.Equal("workbench", customized.Origin);
        Assert.Equal("Private correction", (await reads.DetailAsync(addition.Id, "tenant", default))!.Description);
        var archived = Saved(await writes.SetArchivedAsync(AuthTestApplication.MemberUserId, addition.Id, true, updated.EffectiveVersion!, default));
        Assert.True(archived.IsArchived);
        Assert.Equal("tenant", (await reads.DetailAsync(addition.Id, default))!.Origin);
        var reset = Saved(await writes.ResetAsync(AuthTestApplication.MemberUserId, addition.Id, null, customized.EffectiveVersion!, default));
        Assert.Null(reset.Description);
        Assert.Equal("workbench", reset.Origin);
        Assert.True((await reads.DetailAsync(addition.Id, "tenant", default))!.IsArchived);
        var restored = Saved(await writes.SetArchivedAsync(AuthTestApplication.MemberUserId, addition.Id, false, archived.EffectiveVersion!, default));
        Assert.Equal("Private correction", restored.Description);
        Assert.Equal("Ruby", (await ReadAsync(addition.Id, OtherTenant)).CommonName);
    }

    [Fact]
    public async Task NewInvalidEffectiveCandidateIsValidationAndDoesNotPersistChoices()
    {
        // GIVEN a currently valid mineral and no tenant choices.
        var shared = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var original = await ReadAsync(shared.Id);
        // WHEN a well-shaped species clear makes the final candidate invalid THEN it is a field validation failure.
        var result = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>
        { ["species"] = Clear(), ["description"] = Replace("Must not save") }, original.EffectiveVersion!);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("validation_failed", result.Code);
        Assert.Contains("species", result.Errors.Keys);
        Assert.Equal(original.EffectiveVersion, (await ReadAsync(shared.Id)).EffectiveVersion);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOverrideRejectsUnrelatedEditAndAcceptsValidRepair(bool reset)
    {
        // GIVEN an optional species deliberately cleared before a mineral publication.
        var shared = GemReferenceSamples.Mineral() with
        {
            MaterialKind = "organic",
            Variety = null,
            Sources = [GemReferenceSamples.Source("materialKind"), GemReferenceSamples.Source("commonName"), GemReferenceSamples.Source("species")]
        };
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var choices = new Dictionary<string, GemReferenceFieldOverride> { ["species"] = Clear(), ["description"] = Replace("Keep note") };
        Saved(await OverrideAsync(shared.Id, choices, (await ReadAsync(shared.Id)).EffectiveVersion!));
        var citation = GemReferenceSamples.Source("species");
        await PublishAsync(shared with { MaterialKind = "mineral", Sources = shared.Sources.Where(s => s.Field != "species").Append(citation).ToArray() });
        var invalid = await ReadAsync(shared.Id);
        Assert.True(invalid.NeedsReview);
        Assert.Null(invalid.Species);
        // WHEN editing only description THEN rejection preserves the complete original map and version.
        var result = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>(choices) { ["description"] = Replace("Unrelated") }, invalid.EffectiveVersion!);
        Assert.Equal("needs_review", result.Code);
        Assert.Contains("species", result.Errors.Keys);
        var retained = await ReadAsync(shared.Id);
        Assert.Equal("Keep note", retained.Description);
        Assert.Equal("clear", retained.Overrides["species"].State);
        Assert.Equal(invalid.EffectiveVersion, retained.EffectiveVersion);
        // AND explicit repair or field reset accepts only a valid final candidate with truthful sources.
        var repaired = reset ? Saved(await ResetAsync(shared.Id, "species", invalid.EffectiveVersion!)) :
            Saved(await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>(choices) { ["species"] = Replace("Tenant species") }, invalid.EffectiveVersion!));
        Assert.False(repaired.NeedsReview);
        Assert.Equal("Keep note", repaired.Description);
        if (reset) Assert.Equal(citation.Id, Assert.Single(repaired.EffectiveFields["species"].Sources).Id);
        else Assert.Empty(repaired.EffectiveFields["species"].Sources);
    }

    [Fact]
    public async Task InvalidSourcesAndReservedSharedIdsRejectWithoutMutation()
    {
        // GIVEN shared content, including a retired stable ID.
        var shared = GemReferenceSamples.Mineral() with { IsRetired = true, RetirementExplanation = "Retired sample" };
        await GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        Assert.Equal("duplicate_identity", (await CreateAsync(Addition() with { Id = shared.Id })).Code);
        var original = await ReadAsync(shared.Id);
        // WHEN replacing a field with another field's source THEN nothing persists.
        var rejected = await OverrideAsync(shared.Id, new Dictionary<string, GemReferenceFieldOverride>
        { ["description"] = Replace("Tenant note", GemReferenceSamples.Source("species")) }, original.EffectiveVersion!);
        Assert.Equal(400, rejected.StatusCode);
        Assert.Contains("description", rejected.Errors.Keys);
        Assert.Equal(original.EffectiveVersion, (await ReadAsync(shared.Id)).EffectiveVersion);
        Assert.Empty((await ReadAsync(shared.Id)).Overrides);
    }
}
