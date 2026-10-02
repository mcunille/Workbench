// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceDraftTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task IncompleteDraftSurvivesReloadAndStaleSavePreservesIt()
    {
        // GIVEN an authorized service admin and a new incomplete entry.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        var session = await CreateSessionAsync(application);
        var service = new GemReferenceDraftService(application.WebConnectionString);
        var content = GemReferenceSamples.Mineral() with { Species = null, Sources = [] };
        var draftId = Guid.NewGuid();
        var publishedCount = await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM Gemology.Entries");
        // WHEN saving incomplete content THEN the draft and field errors are returned without publication.
        var saved = await service.SaveAsync(AuthTestApplication.ServiceAdminId, session, draftId,
            new(content.Id, content, null, null), default);
        Assert.Equal(content.Id, saved.EntryId);
        Assert.Null(saved.Content.Species);
        Assert.Contains("species", saved.Errors.Keys);
        Assert.Contains("sources", saved.Errors.Keys);
        var reloaded = await service.ReadAsync(AuthTestApplication.ServiceAdminId, session, draftId, default);
        Assert.Equal(saved.RowVersion, reloaded!.RowVersion);
        Assert.Null(reloaded.Content.Species);
        Assert.Equal(publishedCount, await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM Gemology.Entries"));
        // WHEN updating and then saving the stale version THEN the current draft survives.
        var updated = await service.SaveAsync(AuthTestApplication.ServiceAdminId, session, draftId,
            new(content.Id, content with { CommonName = "Revised" }, saved.RowVersion, null), default);
        Assert.NotEqual(saved.RowVersion, updated.RowVersion);
        Assert.Equal(50044, (await Assert.ThrowsAsync<SqlException>(() => service.SaveAsync(AuthTestApplication.ServiceAdminId,
            session, draftId, new(content.Id, content, saved.RowVersion, null), default))).Number);
        Assert.Contains("Revised", await ServiceAdminIdentityDatabaseTests.ScalarAsync<string>(application.AdminConnectionString,
            "SELECT ContentJson FROM Gemology.Drafts WHERE Id=@id", ("id", draftId)));
    }

    internal static async Task<Guid> CreateSessionAsync(AuthTestApplication application)
    {
        var session = Guid.NewGuid();
        await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(application.AdminConnectionString, """
            INSERT ServiceAdministration.Sessions(Id,AccountId,SecurityVersion,TokenHash,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
            VALUES(@session,@account,1,CRYPT_GEN_RANDOM(32),SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()));
            """, ("session", session), ("account", AuthTestApplication.ServiceAdminId));
        return session;
    }

    [Fact]
    public async Task DraftSaveHoldsPublicationBoundaryThroughCatalogValidation()
    {
        // GIVEN a catalog child read blocked by a competing database transaction.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        var session = await CreateSessionAsync(application);
        await using var blocker = new SqlConnection(application.AdminConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = new SqlCommand("SELECT COUNT(*) FROM Gemology.Aliases WITH(TABLOCKX,HOLDLOCK)", blocker, blockingTransaction))
            await command.ExecuteScalarAsync();
        var content = GemReferenceSamples.Mineral();
        var save = new GemReferenceDraftService(application.WebConnectionString).SaveAsync(AuthTestApplication.ServiceAdminId,
            session, Guid.NewGuid(), new(content.Id, content, null, null), default);
        var blocked = false;
        int lockResult;
        try
        {
            for (var attempt = 0; attempt < 200 && !blocked; attempt++)
            {
                blocked = await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(application.AdminConnectionString,
                    "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id=DB_ID() AND resource_type='OBJECT' AND resource_associated_entity_id=OBJECT_ID('Gemology.Aliases') AND request_status='WAIT'") > 0;
                if (!blocked) await Task.Delay(10);
            }
            // WHEN publication attempts to enter while save validation is reading children THEN it must wait.
            await using var publisher = new SqlConnection(application.AdminConnectionString);
            await publisher.OpenAsync();
            await using var publishingTransaction = (SqlTransaction)await publisher.BeginTransactionAsync();
            await using var probe = new SqlCommand("DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'Gemology.Publication',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; SELECT @result;", publisher, publishingTransaction);
            lockResult = (int)(await probe.ExecuteScalarAsync())!;
            await publishingTransaction.RollbackAsync();
        }
        finally
        {
            await blockingTransaction.CommitAsync();
            await save;
        }
        Assert.True(blocked, "The save must be reading the locked catalog children before publication probes its boundary.");
        Assert.Equal(-1, lockResult);
    }

    [Fact]
    public async Task DisableWhileDraftSaveWaitsForPublicationLockRejectsSave()
    {
        // GIVEN an enabled admin and a competing catalog transaction holding the publication lock.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        var session = await CreateSessionAsync(application);
        await using var blocker = new SqlConnection(application.AdminConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await GemReferenceCurationSql.LockAsync(blocker, transaction, default);
        var save = new GemReferenceDraftService(application.WebConnectionString).SaveAsync(AuthTestApplication.ServiceAdminId, session,
            Guid.NewGuid(), new(GemReferenceSamples.Mineral().Id, GemReferenceSamples.Mineral(), null, null), default);
        var waitStarted = false;
        for (var attempt = 0; attempt < 200 && !waitStarted; attempt++)
        {
            waitStarted = await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(application.AdminConnectionString,
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id=DB_ID() AND resource_type='APPLICATION' AND request_status='WAIT'") > 0;
            if (!waitStarted) await Task.Delay(10);
        }
        Assert.True(waitStarted, "The draft save must be waiting on the held catalog lock before revocation.");
        // WHEN authority is disabled while the save waits, THEN acquiring the catalog lock cannot resurrect it.
        await application.MaintainServiceAdminAsync("DisableServiceAdmin");
        await transaction.CommitAsync();
        Assert.Equal(50041, (await Assert.ThrowsAsync<SqlException>(() => save)).Number);
        Assert.Equal(0, await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM Gemology.Drafts"));
    }
}
