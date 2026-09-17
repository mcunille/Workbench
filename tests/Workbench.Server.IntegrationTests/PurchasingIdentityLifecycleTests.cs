// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Tenancy;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    private const string DraftPath = "/api/beta/purchase-order-drafts";
    private static DraftContent Empty => new(null, null, null, null, [], [], null, null, null, null, null, null, null, null);
    private static SupplierContent Contact => new("Supplier", "Contact", "contact@example.test", "+1 555 0100 ext 2", "https://example.test", "Line one\nLine two");
    private static async Task<SaveSupplierResponse> SupplierSave(HttpClient client, SupplierContent? content = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", new CreateSupplierRequest(Guid.NewGuid(), content ?? Contact));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
    }
    private static async Task<SaveDraftOrderResponse> DraftSave(HttpClient client, DraftContent draft)
    {
        var response = await SendAsync(client, HttpMethod.Post, DraftPath, new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
    }
    private static Task<DraftOrderResponse?> Read(HttpClient client, Guid id) => client.GetFromJsonAsync<DraftOrderResponse>($"{DraftPath}/{id}");
    [Fact]
    public async Task DirectoryEditsAndArchiveLeaveSavedSnapshotsAndPlatformsIndependent()
    {
        // GIVEN two purchases linked to one reusable supplier on different platforms.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient(); await LoginAsync(client);
        var supplier = await SupplierSave(client);
        var draft = Empty with { SupplierId = supplier.SupplierId, SupplierName = "Reviewed supplier", SupplierEmail = "saved@example.test", Platform = "Instagram", SupplierOrderReference = "External 123" };
        var first = await DraftSave(client, draft); var second = await DraftSave(client, draft with { Platform = "Gem Rock Auctions" });
        // WHEN directory details change and that supplier is archived.
        var edit = await SendAsync(client, HttpMethod.Put, $"/api/beta/suppliers/{supplier.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), supplier.SavedVersion, Contact with { Name = "Current directory name", Email = "new@example.test" }));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        var archive = await SendAsync(client, HttpMethod.Post, $"/api/beta/suppliers/{supplier.SupplierId}/archive", new ArchiveSupplierRequest(Guid.NewGuid(), edited.SavedVersion, true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        // THEN the original snapshots, references and versions remain unchanged and archived state is visible.
        var saved = (await Read(client, first.DraftOrderId))!;
        Assert.Equal(JsonSerializer.Serialize(draft), JsonSerializer.Serialize(saved.Draft)); Assert.Equal(first.SavedVersion, saved.Version); Assert.True(saved.SupplierIsArchived);
        Assert.Equal("Gem Rock Auctions", (await Read(client, second.DraftOrderId))!.Draft.Platform);
        Assert.Empty((await client.GetFromJsonAsync<SupplierPageResponse>("/api/beta/suppliers"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<SupplierPageResponse>("/api/beta/suppliers?includeArchived=true"))!.Items);
        // AND existing links remain editable, but new links to archived suppliers are refused.
        var update = await SendAsync(client, HttpMethod.Put, $"{DraftPath}/{first.DraftOrderId}", new UpdateDraftOrderRequest(Guid.NewGuid(), saved.Version, draft with { Platform = "Retail" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var rejected = await SendAsync(client, HttpMethod.Post, DraftPath, new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("supplier_selection_conflict", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [Fact]
    public async Task SupplierReceiptsReplayAndStaleOrChangedRequestsRetainSavedDetails()
    {
        // GIVEN a successful supplier create whose response may have been lost.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient(); await LoginAsync(client);
        var request = new CreateSupplierRequest(Guid.NewGuid(), Contact);
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request);
        var saved = (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        var update = await SendAsync(client, HttpMethod.Put, $"/api/beta/suppliers/{saved.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), saved.SavedVersion, Contact with { Name = "Changed" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        // WHEN creation is retried after an edit THEN only its original compact receipt is returned.
        var replay = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request);
        var receipt = (await replay.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        Assert.True(receipt.Replayed); Assert.Equal(saved.SavedVersion, receipt.SavedVersion); Assert.Equal(saved.CompletedAtUtc, receipt.CompletedAtUtc);
        // AND changed-input reuse and stale edits conflict without reverting directory details.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request with { Supplier = Contact with { Phone = "Other" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/beta/suppliers/{saved.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), saved.SavedVersion, Contact))).StatusCode);
        Assert.Equal("Changed", (await client.GetFromJsonAsync<SupplierResponse>($"/api/beta/suppliers/{saved.SupplierId}"))!.Supplier.Name);
    }
    [Fact]
    public async Task ReferencesArePermanentAndSearchTreatsWildcardsLiterally()
    {
        // GIVEN saved purchases with distinct identity, title, supplier and external references.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient(); await LoginAsync(client);
        var first = await DraftSave(client, Empty with { Title = "Literal %_[ match", SupplierName = "Snapshot name", SupplierOrderReference = "External-ABC", Platform = "Unique platform" });
        var second = await DraftSave(client, Empty);
        Assert.Equal("PO-000001", (await Read(client, first.DraftOrderId))!.PoReference);
        Assert.Equal("PO-000002", (await Read(client, second.DraftOrderId))!.PoReference);
        // WHEN searching each supported field THEN the matching draft is found with literal case-insensitive text.
        foreach (var query in new[] { "po-000001", "%_[", "snapshot", "external-abc" })
            Assert.Equal(first.DraftOrderId, Assert.Single((await client.GetFromJsonAsync<DraftOrderPageResponse>(DraftPath + "?query=" + Uri.EscapeDataString(query)))!.Items).Id);
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(DraftPath + "?query=unique%20platform"))!.Items);
        // WHEN the first purchase is deleted THEN a later purchase receives a new number and content is cleared.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{DraftPath}/{first.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), first.SavedVersion))).StatusCode);
        var third = await DraftSave(client, Empty); Assert.Equal("PO-000003", (await Read(client, third.DraftOrderId))!.PoReference);
        await using var admin = new SqlConnection(application.AdminConnectionString); await admin.OpenAsync();
        await using var read = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id AND PoNumber=1 AND IsDeleted=1 AND SupplierId IS NULL AND SupplierName IS NULL AND SupplierEmail IS NULL AND SupplierOrderReference IS NULL AND Platform IS NULL", admin);
        read.Parameters.AddWithValue("@id", first.DraftOrderId); Assert.Equal(1, await read.ExecuteScalarAsync());
    }
    [Fact]
    public async Task V1ReceiptsSurviveUpgradeAndUnmatchedOldWritesRequireReload()
    {
        // GIVEN successful V1 create/update receipts and a deleted tombstone in the immediately preceding schema.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddDraftSupplierOrders");
        using var client = application.CreateClient(); await LoginAsync(client);
        var request = new HistoricalCreateRequest(Guid.NewGuid(), new("Retained", "Old supplier", null, null, [], []));
        var receipt = await HistoricalSave(application, "Create", request.RequestId, null, null, request.Draft);
        var updateRequest = new HistoricalUpdateRequest(Guid.NewGuid(), receipt.SavedVersion, request.Draft with { Title = "Updated retained" });
        var updated = await HistoricalSave(application, "Update", updateRequest.RequestId, receipt.DraftOrderId, receipt.SavedVersion, updateRequest.Draft);
        var deletedRequest = new HistoricalCreateRequest(Guid.NewGuid(), request.Draft);
        var deleted = await HistoricalSave(application, "Create", deletedRequest.RequestId, null, null, deletedRequest.Draft);
        await HistoricalSave(application, "Delete", Guid.NewGuid(), deleted.DraftOrderId, deleted.SavedVersion, null);
        // WHEN the forward migration backfills active purchase numbers.
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, default);
        // THEN saved snapshots and timestamps survive, while matching old receipts keep their original versions.
        var detail = (await Read(client, receipt.DraftOrderId))!; Assert.Equal("PO-000001", detail.PoReference); Assert.Equal("Updated retained", detail.Draft.Title); Assert.Null(detail.Draft.Platform);
        var replay = (await (await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", request)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        Assert.True(replay.Replayed); Assert.Equal(receipt.SavedVersion, replay.SavedVersion); Assert.Equal(receipt.CompletedAtUtc, replay.CompletedAtUtc);
        var updateReplay = (await (await SendAsync(client, HttpMethod.Put, $"/api/purchase-order-drafts/{receipt.DraftOrderId}", updateRequest)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        Assert.True(updateReplay.Replayed); Assert.Equal(updated.SavedVersion, updateReplay.SavedVersion);
        Assert.True((await (await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", deletedRequest)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        // AND unmatched V1 writes cannot silently clear the new contract.
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", request with { RequestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await SendAsync(client, HttpMethod.Put, $"/api/purchase-order-drafts/{receipt.DraftOrderId}", updateRequest with { RequestId = Guid.NewGuid(), ExpectedVersion = detail.Version })).StatusCode);
    }
    [Fact]
    public async Task UnicodeContactAtPublishedLimitsCanBeSaved()
    {
        // GIVEN valid contact fields at their published UTF-16 limits, whose canonical JSON escapes non-ASCII text.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient(); await LoginAsync(client);
        var contact = new SupplierContent(new string('名', 200), new string('人', 200), new string('文', 240) + "@example.test", new string('電', 100), "https://example.test/", new string('住', 2000));
        // WHEN saved through HTTP THEN the bounded canonical envelope still accommodates the entire valid contact.
        var saved = await SupplierSave(client, contact);
        Assert.Equal(contact, (await client.GetFromJsonAsync<SupplierResponse>($"/api/beta/suppliers/{saved.SupplierId}"))!.Supplier);
    }

    private sealed record HistoricalCreateRequest(Guid RequestId, ReceiptDraftContentV1 Draft);
    private sealed record HistoricalUpdateRequest(Guid RequestId, string ExpectedVersion, ReceiptDraftContentV1 Draft);

    private static async Task<SaveDraftOrderResponse> HistoricalSave(AuthTestApplication application, string operation,
        Guid requestId, Guid? id, string? version, ReceiptDraftContentV1? draft)
    {
        await using var connection = new SqlConnection(application.WebConnectionString);
        await connection.OpenAsync();
        await application.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(connection, AuthTestApplication.TenantId, default);
        await using var command = new SqlCommand($"Purchasing.{operation}DraftOrder", connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", requestId);
        command.Parameters.AddWithValue("@ActorUserId", AuthTestApplication.MemberUserId);
        if (operation == "Delete")
        {
            command.Parameters.AddWithValue("@DraftOrderId", id!.Value);
            command.Parameters.AddWithValue("@ExpectedRowVersion", Convert.FromBase64String(version!));
        }
        else command.Parameters.AddWithValue("@CanonicalInputJson", ReceiptDraftOrderInputV1.Canonical(operation, id, version, ReceiptDraftOrderInputV1.Normalize(draft!)));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetGuid(reader.GetOrdinal("RequestId")), reader.GetBoolean(reader.GetOrdinal("Replayed")),
            reader.GetGuid(reader.GetOrdinal("DraftOrderId")), Convert.ToBase64String((byte[])reader["SavedVersion"]),
            DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))));
    }
}
