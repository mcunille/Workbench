// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageMagick;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPhotoCommitTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationFailureCanRetryWithoutDuplicatingThePhotoOrRetention(bool failAfterCommit)
    {
        // GIVEN a saved photograph and a filesystem provider, with failure injection initially disabled.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new PhotoStorage();
        var failure = new FinalPhotoCommitFailure(failAfterCommit);
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(failure));
        }));
        using var client = factory.CreateClient();
        await ItemPhotoEndpointTests.LoginAsync(client);
        using var created = await ItemPhotoEndpointTests.SendJsonAsync(client, HttpMethod.Post, "/api/items",
            new { creationRequestId = Guid.NewGuid(), name = "Commit recovery specimen" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var path = created.Headers.Location!.ToString();
        var item = await client.GetFromJsonAsync<JsonElement>(path);
        var itemId = item.GetProperty("id").GetGuid();
        using var initial = await ItemPhotoEndpointTests.UploadAsync(client, path,
            item.GetProperty("version").GetString()!, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var original = await client.GetFromJsonAsync<JsonElement>(path);
        var originalPhotoId = original.GetProperty("photo").GetProperty("id").GetGuid();
        var expectedVersion = original.GetProperty("version").GetString()!;
        var requestId = Guid.NewGuid();
        failure.Arm(requestId);

        // WHEN the replacement loses confirmation immediately before or after the real final SQL commit.
        using var interrupted = await ItemPhotoEndpointTests.UploadAsync(client, path, expectedVersion, requestId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        Assert.Equal(1, failure.InjectedFailures);

        // THEN another session sees the old complete pair after rollback, or the new complete pair after commit.
        using var nextSession = factory.CreateClient();
        await ItemPhotoEndpointTests.LoginAsync(nextSession);
        var afterFailure = await nextSession.GetFromJsonAsync<JsonElement>(path);
        var failedState = await ReadStateAsync(application, itemId, requestId);
        Assert.Equal(failAfterCommit ? 1 : 0, failedState.OperationState);
        Assert.Equal(failAfterCommit ? 2 : 1, failedState.Photos);
        Assert.Equal(2, failedState.Operations);
        Assert.Equal(failAfterCommit ? 2 : 0, failedState.RetentionJobs);
        Assert.Equal(failAfterCommit ? failedState.OperationId : originalPhotoId,
            afterFailure.GetProperty("photo").GetProperty("id").GetGuid());
        Assert.Equal(failAfterCommit ? failedState.ResultVersion : expectedVersion,
            afterFailure.GetProperty("version").GetString());
        await AssertDeliveredPairAsync(nextSession, afterFailure);

        // WHEN the independent session retries the exact request UUID, expected version and bytes.
        using var retry = await ItemPhotoEndpointTests.UploadAsync(nextSession, path, expectedVersion, requestId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var result = await retry.Content.ReadFromJsonAsync<JsonElement>();
        using var replay = await ItemPhotoEndpointTests.UploadAsync(nextSession, path, expectedVersion, requestId);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var repeated = await replay.Content.ReadFromJsonAsync<JsonElement>();

        // THEN both responses are the original durable result with one replacement and exactly two retirement jobs.
        var finalState = await ReadStateAsync(application, itemId, requestId);
        Assert.Equal(1, failure.InjectedFailures);
        Assert.Equal(1, finalState.OperationState);
        Assert.Equal(2, finalState.Photos);
        Assert.Equal(2, finalState.Operations);
        Assert.Equal(2, finalState.RetentionJobs);
        Assert.Equal(4, finalState.AvailableRevisions);
        Assert.Equal(2, finalState.SavedAuditEvents);
        Assert.Equal(failedState.OperationId, finalState.OperationId);
        Assert.Equal(requestId, result.GetProperty("requestId").GetGuid());
        Assert.Equal(finalState.OperationId, result.GetProperty("photoId").GetGuid());
        Assert.Equal(finalState.ResultVersion, result.GetProperty("version").GetString());
        Assert.Equal(result.GetRawText(), repeated.GetRawText());
        if (failAfterCommit)
            Assert.Equal(failedState.ResultVersion, finalState.ResultVersion);
        var current = await nextSession.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(finalState.OperationId, current.GetProperty("photo").GetProperty("id").GetGuid());
        Assert.Equal("Commit recovery specimen", current.GetProperty("name").GetString());
        await AssertDeliveredPairAsync(nextSession, current);
    }

    private static async Task AssertDeliveredPairAsync(HttpClient client, JsonElement item)
    {
        foreach (var property in new[] { "detailUrl", "thumbnailUrl" })
        {
            using var response = await client.GetAsync(item.GetProperty("photo").GetProperty(property).GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            using var image = new MagickImage(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(MagickFormat.WebP, image.Format);
            Assert.True(image.Width > 0 && image.Height > 0);
        }
    }

    private static async Task<PhotoState> ReadStateAsync(AuthTestApplication application, Guid itemId, Guid requestId)
    {
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT o.[Id], o.[State], o.[ResultVersion],
                (SELECT COUNT(*) FROM [Inventory].[ItemPhotos] p WHERE p.[ItemId]=@itemId),
                (SELECT COUNT(*) FROM [Inventory].[ItemPhotoOperations] p WHERE p.[ItemId]=@itemId),
                (SELECT COUNT(*) FROM [Operations].[WorkItems] w WHERE w.[Kind]=1),
                (SELECT COUNT(*) FROM [Storage].[Revisions] r WHERE r.[State]=1),
                (SELECT COUNT(*) FROM [Security].[TenantSecurityAuditEvents] a WHERE a.[TargetId]=@itemId AND a.[Action]='inventory.photo.saved')
            FROM [Inventory].[ItemPhotoOperations] o WHERE o.[ItemId]=@itemId AND o.[RequestId]=@requestId;
            """, connection);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PhotoState(reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : Convert.ToBase64String((byte[])reader[2]),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    }

    private sealed record PhotoState(Guid OperationId, int OperationState, string? ResultVersion,
        int Photos, int Operations, int RetentionJobs, int AvailableRevisions, int SavedAuditEvents);

    private sealed class FinalPhotoCommitFailure(bool afterCommit) : DbTransactionInterceptor
    {
        private Guid? requestId;
        private Guid? finalTransactionId;
        public int InjectedFailures { get; private set; }

        public void Arm(Guid operationRequestId) => requestId = operationRequestId;

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (requestId is not { } target || InjectedFailures != 0)
                return result;
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM [Inventory].[ItemPhotoOperations] WHERE [RequestId]=@requestId AND [State]=1";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@requestId";
            parameter.Value = target;
            command.Parameters.Add(parameter);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                return result;
            finalTransactionId = eventData.TransactionId;
            if (!afterCommit)
            {
                InjectedFailures++;
                throw new IOException("Injected failure before photo finalization commit.");
            }
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (afterCommit && finalTransactionId == eventData.TransactionId && InjectedFailures == 0)
            {
                InjectedFailures++;
                throw new IOException("Injected lost confirmation after photo finalization commit.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class PhotoStorage : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-photo-commit-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public PhotoStorage()
        {
            Directory.CreateDirectory(root);
            Store = new FileSystemBlobStore(root);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
