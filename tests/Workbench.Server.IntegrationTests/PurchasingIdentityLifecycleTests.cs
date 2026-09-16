// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    private const string DraftPath = "/api/v2/purchase-order-drafts";
    private static DraftContentV2 Empty => new(null, null, null, null, [], [], null, null, null, null, null, null, null, null);
    private static SupplierContent Contact => new("Supplier", "Contact", "contact@example.test", "+1 555 0100 ext 2", "https://example.test", "Line one\nLine two");
    private static async Task<SaveSupplierResponse> SupplierSave(HttpClient client, SupplierContent? content = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/suppliers", new CreateSupplierRequest(Guid.NewGuid(), content ?? Contact));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
    }
    private static async Task<SaveDraftOrderResponse> DraftSave(HttpClient client, DraftContentV2 draft)
    {
        var response = await SendAsync(client, HttpMethod.Post, DraftPath, new CreateDraftOrderRequestV2(Guid.NewGuid(), draft));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
    }
    private static Task<DraftOrderResponseV2?> Read(HttpClient client, Guid id) => client.GetFromJsonAsync<DraftOrderResponseV2>($"{DraftPath}/{id}");
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
        var edit = await SendAsync(client, HttpMethod.Put, $"/api/suppliers/{supplier.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), supplier.SavedVersion, Contact with { Name = "Current directory name", Email = "new@example.test" }));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        var archive = await SendAsync(client, HttpMethod.Post, $"/api/suppliers/{supplier.SupplierId}/archive", new ArchiveSupplierRequest(Guid.NewGuid(), edited.SavedVersion, true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        // THEN the original snapshots, references and versions remain unchanged and archived state is visible.
        var saved = (await Read(client, first.DraftOrderId))!;
        Assert.Equal(JsonSerializer.Serialize(draft), JsonSerializer.Serialize(saved.Draft)); Assert.Equal(first.SavedVersion, saved.Version); Assert.True(saved.SupplierIsArchived);
        Assert.Equal("Gem Rock Auctions", (await Read(client, second.DraftOrderId))!.Draft.Platform);
        Assert.Empty((await client.GetFromJsonAsync<SupplierPageResponse>("/api/suppliers"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<SupplierPageResponse>("/api/suppliers?includeArchived=true"))!.Items);
        // AND existing links remain editable, but new links to archived suppliers are refused.
        var update = await SendAsync(client, HttpMethod.Put, $"{DraftPath}/{first.DraftOrderId}", new UpdateDraftOrderRequestV2(Guid.NewGuid(), saved.Version, draft with { Platform = "Retail" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var rejected = await SendAsync(client, HttpMethod.Post, DraftPath, new CreateDraftOrderRequestV2(Guid.NewGuid(), draft));
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
        var response = await SendAsync(client, HttpMethod.Post, "/api/suppliers", request);
        var saved = (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        var update = await SendAsync(client, HttpMethod.Put, $"/api/suppliers/{saved.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), saved.SavedVersion, Contact with { Name = "Changed" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        // WHEN creation is retried after an edit THEN only its original compact receipt is returned.
        var replay = await SendAsync(client, HttpMethod.Post, "/api/suppliers", request);
        var receipt = (await replay.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        Assert.True(receipt.Replayed); Assert.Equal(saved.SavedVersion, receipt.SavedVersion); Assert.Equal(saved.CompletedAtUtc, receipt.CompletedAtUtc);
        // AND changed-input reuse and stale edits conflict without reverting directory details.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/suppliers", request with { Supplier = Contact with { Phone = "Other" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/suppliers/{saved.SupplierId}", new UpdateSupplierRequest(Guid.NewGuid(), saved.SavedVersion, Contact))).StatusCode);
        Assert.Equal("Changed", (await client.GetFromJsonAsync<SupplierResponse>($"/api/suppliers/{saved.SupplierId}"))!.Supplier.Name);
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
            Assert.Equal(first.DraftOrderId, Assert.Single((await client.GetFromJsonAsync<DraftOrderPageResponseV2>(DraftPath + "?query=" + Uri.EscapeDataString(query)))!.Items).Id);
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponseV2>(DraftPath + "?query=unique%20platform"))!.Items);
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
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), new("Retained", "Old supplier", null, null, [], []));
        var original = await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", request);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var receipt = (await original.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var updateRequest = new UpdateDraftOrderRequest(Guid.NewGuid(), receipt.SavedVersion, request.Draft with { Title = "Updated retained" });
        var update = await SendAsync(client, HttpMethod.Put, $"/api/purchase-order-drafts/{receipt.DraftOrderId}", updateRequest);
        var updated = (await update.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var deletedRequest = new CreateDraftOrderRequest(Guid.NewGuid(), request.Draft);
        var doomed = await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", deletedRequest);
        var deleted = (await doomed.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"/api/purchase-order-drafts/{deleted.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), deleted.SavedVersion))).StatusCode);
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
        Assert.Equal(contact, (await client.GetFromJsonAsync<SupplierResponse>($"/api/suppliers/{saved.SupplierId}"))!.Supplier);
    }
}
