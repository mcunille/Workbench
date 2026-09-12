// Copyright (c) 2026 The White Stag Collection.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualBasic.FileIO;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemAcquisitionExportTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CsvV2PreservesOptionalFactsEveryMethodAndPartialDatesWithReversibleText()
    {
        // GIVEN an unlinked piece and all supported methods with unknown, year, month and day dates.
        await using var context = await Context.CreateAsync(sqlServer);
        var unlinked = await CreateItemAsync(context.Client);
        string[] methods = ["Purchase", "Gift", "Inheritance", "Trade", "Other", "Unknown"];
        string[] sources = ["=1+2", "+Family", "-Source", "@Gift", "'Existing", "蓝, \"source\""];
        var expected = new Dictionary<Guid, (ItemAcquisitionResponse Acquisition, string Source, string Notes, string Precision)>();
        for (var index = 0; index < methods.Length; index++)
        {
            var item = await CreateItemAsync(context.Client);
            int? year = index % 4 == 0 ? null : 2024;
            int? month = index % 4 < 2 ? null : 2;
            int? day = index % 4 < 3 ? null : 29;
            var notes = sources[index] + "\r\nSecond, \"line\"";
            var acquisition = await context.CreateAcquisitionAsync(item, methods[index], sources[index], year, month, day, notes);
            expected[item.Id] = (acquisition, sources[index], notes, day.HasValue ? "day" : month.HasValue ? "month" : year.HasValue ? "year" : "unknown");
        }

        // WHEN independently parsing the completed CSV with a standard CSV reader.
        using var response = await context.ExportAsync(package: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var parser = new TextFieldParser(stream, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");

        // THEN existing columns retain their order and acquisition fields preserve exact optional values.
        Assert.Equal("schema_version,exported_at_utc,scope,item_id,tracking_kind,name,notes,location,is_archived,created_at_utc,archived_at_utc,acquisition_id,acquisition_method,acquisition_source,acquisition_date_precision,acquisition_year,acquisition_month,acquisition_day,acquisition_notes".Split(','), parser.ReadFields());
        var timestamps = new HashSet<string>();
        var count = 0;
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!;
            count++;
            Assert.Equal(19, row.Length);
            Assert.Equal("2", row[0]);
            timestamps.Add(row[1]);
            var id = Guid.Parse(row[3]);
            if (id == unlinked.Id) { Assert.All(row[11..], value => Assert.Empty(value)); continue; }
            var saved = expected[id];
            var acquisition = saved.Acquisition.Acquisition!;
            Assert.Equal(acquisition.Id.ToString("D"), row[11]);
            Assert.Equal(acquisition.Method, row[12]);
            Assert.Equal(saved.Source, Decode(row[13]));
            Assert.Equal(saved.Precision, row[14]);
            Assert.Equal(acquisition.Year?.ToString() ?? "", row[15]);
            Assert.Equal(acquisition.Month?.ToString() ?? "", row[16]);
            Assert.Equal(acquisition.Day?.ToString() ?? "", row[17]);
            Assert.Equal(saved.Notes, Decode(row[18]));
        }
        Assert.Equal(7, count);
        Assert.Single(timestamps);
        Assert.Equal(0, context.Store.Reads);
    }

    [Theory]
    [InlineData("active", 2)]
    [InlineData("all", 3)]
    public async Task PackageDeduplicatesSharedDocumentsAndDisclosesOnlySelectedRelationships(string scope, int selected)
    {
        // GIVEN three shared pieces, one archived, and another tenant's acquisition facts and paperwork.
        await using var context = await Context.CreateAsync(sqlServer);
        var first = await CreateItemAsync(context.Client);
        var second = await CreateItemAsync(context.Client);
        var archived = await CreateItemAsync(context.Client);
        var saved = await context.CreateAcquisitionAsync(first, "Gift", "=Family", 2020, null, null, "Literal\nnotes");
        foreach (var item in new[] { second, archived })
        {
            saved = (await context.Client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{first.Id}/acquisition"))!;
            using var link = await SendAsync(context.Client, HttpMethod.Put, $"/api/items/{item.Id}/acquisition-link",
                new LinkAcquisitionRequest(item.Version, null, null, saved.Acquisition!.Id, saved.Acquisition.Version));
            Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        }
        var document = await context.UploadAsync(first.Id, "../蓝 receipt.pdf");
        var currentArchived = (await context.Client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{archived.Id}"))!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(context.Client, HttpMethod.Post, $"/api/items/{archived.Id}/archive", new { expectedVersion = currentArchived.Version })).StatusCode);
        using var other = context.Factory.CreateClient();
        await LoginAsync(other, "other@example.com");
        var foreign = await CreateItemAsync(other);
        using var foreignCreated = await SendAsync(other, HttpMethod.Post, $"/api/items/{foreign.Id}/acquisition",
            new CreateAcquisitionRequest(Guid.NewGuid(), foreign.Version, "Purchase", "Foreign private source", null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, foreignCreated.StatusCode);
        var foreignSaved = (await foreignCreated.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var foreignPath = $"/api/items/{foreign.Id}/acquisition/{foreignSaved.Acquisition!.Id}/documents";
        using var foreignUpload = await AcquisitionDocumentEndpointTests.UploadAsync(other, foreignPath, foreignSaved, Guid.NewGuid(), PhotoFixture.Png(red: 0, blue: 255), "Foreign private receipt");
        Assert.Equal(HttpStatusCode.OK, foreignUpload.StatusCode);

        // WHEN preparing the chosen scope.
        using var response = await context.ExportAsync(scope: scope);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var manifestText = await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json");
        using var manifest = JsonDocument.Parse(manifestText);

        // THEN one acquisition and one exact original document map only to the selected identities.
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("package_version").GetInt32());
        Assert.Equal(1, root.GetProperty("acquisition_count").GetInt32());
        Assert.Equal(1, root.GetProperty("document_count").GetInt32());
        var acquisition = Assert.Single(root.GetProperty("acquisitions").EnumerateArray());
        Assert.Equal(saved.Acquisition!.Id, acquisition.GetProperty("acquisition_id").GetGuid());
        Assert.Equal("=Family", acquisition.GetProperty("source").GetString());
        Assert.Equal("year", acquisition.GetProperty("date_precision").GetString());
        Assert.Equal(JsonValueKind.Null, acquisition.GetProperty("month").ValueKind);
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(selected, items.Length);
        Assert.All(items, item => Assert.Equal(saved.Acquisition.Id, item.GetProperty("acquisition_id").GetGuid()));
        Assert.Equal(items.Select(item => item.GetProperty("item_id").GetGuid()), acquisition.GetProperty("included_item_ids").EnumerateArray().Select(item => item.GetGuid()));
        var exported = Assert.Single(acquisition.GetProperty("documents").EnumerateArray());
        Assert.Equal(document.Id, exported.GetProperty("document_id").GetGuid());
        Assert.Equal("../蓝 receipt.pdf", exported.GetProperty("label").GetString());
        Assert.Equal($"documents/{saved.Acquisition.Id:D}/{document.Id:D}.png", exported.GetProperty("path").GetString());
        Assert.Equal("image/png", exported.GetProperty("media_type").GetString());
        var expected = PhotoFixture.Png();
        Assert.Equal(expected.Length, exported.GetProperty("byte_length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), exported.GetProperty("sha256").GetString(), ignoreCase: true);
        var entry = Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("documents/", StringComparison.Ordinal));
        using var copied = new MemoryStream();
        await entry.Open().CopyToAsync(copied);
        Assert.Equal(expected, copied.ToArray());
        Assert.DoesNotContain(foreign.Id.ToString("D"), manifestText);
        Assert.DoesNotContain("Foreign private source", manifestText);
        Assert.DoesNotContain("Foreign private receipt", manifestText);
        if (scope == "active") Assert.DoesNotContain(archived.Id.ToString("D"), manifestText);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("unreadable")]
    public async Task RequiredDocumentFailureRejectsWholePackageAndRetryRecovers(string failure)
    {
        // GIVEN a committed document with provider failure or bytes inconsistent with its metadata.
        await using var context = await Context.CreateAsync(sqlServer);
        var item = await CreateItemAsync(context.Client);
        await context.CreateAcquisitionAsync(item);
        await context.UploadAsync(item.Id);
        context.Store.Failure = failure;

        // WHEN preparing ZIP and CSV from that state.
        using var failed = await context.ExportAsync();
        using var csv = await context.ExportAsync(package: false);

        // THEN no partial ZIP is offered, while CSV remains available without reading documents.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Null(failed.Content.Headers.ContentDisposition);
        Assert.Contains("export_preparation_failed", await failed.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Injected", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        // WHEN storage recovers THEN a fresh complete package can be prepared.
        context.Store.Failure = null;
        using var retry = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await retry.Content.ReadAsByteArrayAsync()));
        Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("documents/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryUnavailableDocumentRejectsPackageBeforeProviderRead()
    {
        // GIVEN accepted recovery marking a current document unavailable even when its blob exists.
        await using var context = await Context.CreateAsync(sqlServer);
        var item = await CreateItemAsync(context.Client);
        await context.CreateAcquisitionAsync(item);
        var document = await context.UploadAsync(item.Id);
        await using var sql = new SqlConnection(context.Application.AdminConnectionString);
        await sql.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc)
            SELECT TenantId,RevisionId,NEWID(),1,'Missing',SYSUTCDATETIME()
            FROM Inventory.AcquisitionDocuments WHERE Id=@document;
            """, sql);
        command.Parameters.AddWithValue("@document", document.Id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var reads = context.Store.Reads;
        // WHEN preparing a package THEN authoritative unavailability cannot become an empty document list.
        using var response = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(reads, context.Store.Reads);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("sha256")]
    [InlineData("media-type")]
    [InlineData("no-current-revision")]
    [InlineData("different-current-revision")]
    [InlineData("deleted-attachment")]
    [InlineData("purged-revision")]
    public async Task InconsistentDocumentMetadataRejectsPackageBeforeProviderRead(string mismatch)
    {
        // GIVEN committed paperwork whose attachment or document metadata no longer agrees with its revision.
        await using var context = await Context.CreateAsync(sqlServer);
        var item = await CreateItemAsync(context.Client);
        await context.CreateAcquisitionAsync(item);
        var document = await context.UploadAsync(item.Id);
        await using var sql = new SqlConnection(context.Application.AdminConnectionString);
        await sql.OpenAsync();
        // Published revision identity is immutable under Storage.ProtectRevision, including for db_owner.
        // Change the document side of those comparisons without disabling the trigger or SQL constraints.
        var statement = mismatch switch
        {
            "length" => "UPDATE Inventory.AcquisitionDocuments SET Length=Length+1 WHERE Id=@document;",
            "sha256" => "UPDATE Inventory.AcquisitionDocuments SET Sha256=REPLICATE('0',64) WHERE Id=@document;",
            "media-type" => "UPDATE Inventory.AcquisitionDocuments SET MediaType='application/pdf',Extension='pdf' WHERE Id=@document;",
            "no-current-revision" => "UPDATE a SET CurrentRevisionId=NULL FROM Storage.Attachments a JOIN Inventory.AcquisitionDocuments d ON d.AttachmentId=a.Id WHERE d.Id=@document;",
            "deleted-attachment" => "UPDATE a SET DeletedAtUtc=SYSUTCDATETIME(),DeleteAfterUtc=DATEADD(day,7,SYSUTCDATETIME()) FROM Storage.Attachments a JOIN Inventory.AcquisitionDocuments d ON d.AttachmentId=a.Id WHERE d.Id=@document;",
            "purged-revision" => "UPDATE r SET State=3 FROM Storage.Revisions r JOIN Inventory.AcquisitionDocuments d ON d.RevisionId=r.Id WHERE d.Id=@document;",
            _ => """
                DECLARE @replacement uniqueidentifier=NEWID();
                INSERT Storage.Revisions(Id,TenantId,AttachmentId,OperationId,ActorUserId,PreviousRevisionId,ProviderAlias,Source,MediaType,Length,Sha256,State,CreatedAtUtc)
                SELECT @replacement,r.TenantId,r.AttachmentId,NEWID(),r.ActorUserId,r.Id,r.ProviderAlias,r.Source,r.MediaType,r.Length,r.Sha256,r.State,SYSUTCDATETIME()
                FROM Storage.Revisions r JOIN Inventory.AcquisitionDocuments d ON d.RevisionId=r.Id WHERE d.Id=@document;
                UPDATE a SET CurrentRevisionId=@replacement FROM Storage.Attachments a
                JOIN Inventory.AcquisitionDocuments d ON d.AttachmentId=a.Id WHERE d.Id=@document;
                """
        };
        await using var corrupt = new SqlCommand(statement, sql);
        corrupt.Parameters.AddWithValue("@document", document.Id);
        Assert.Equal(mismatch == "different-current-revision" ? 2 : 1, await corrupt.ExecuteNonQueryAsync());
        var reads = context.Store.Reads;

        // WHEN preparing the ZIP THEN metadata validation fails before any provider stream is acquired.
        using var response = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(reads, context.Store.Reads);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Contains("export_preparation_failed", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(document.Id.ToString("D"), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OrphanAcquisitionsRemovedDocumentsAndPendingUploadsAreExcluded()
    {
        // GIVEN retained paperwork on an orphan acquisition and a removed file on a linked acquisition.
        await using var context = await Context.CreateAsync(sqlServer);
        var orphanItem = await CreateItemAsync(context.Client);
        var orphan = await context.CreateAcquisitionAsync(orphanItem, source: "Orphan source");
        var orphanDocument = await context.UploadAsync(orphanItem.Id);
        orphan = (await context.Client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{orphanItem.Id}/acquisition"))!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(context.Client, HttpMethod.Put, $"/api/items/{orphanItem.Id}/acquisition-link",
            new LinkAcquisitionRequest(orphan.ItemVersion, orphan.Acquisition!.Id, orphan.Acquisition.Version, null, null))).StatusCode);
        var item = await CreateItemAsync(context.Client);
        var saved = await context.CreateAcquisitionAsync(item);
        var removed = await context.UploadAsync(item.Id);
        var path = $"/api/items/{item.Id}/acquisition/{saved.Acquisition!.Id}/documents";
        var listing = (await context.Client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(context.Client, HttpMethod.Delete, $"{path}/{removed.Id}",
            new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), listing.ItemVersion, listing.AcquisitionVersion, removed.Version, null))).StatusCode);

        // AND provider publication loses its response, leaving a real upload reservation pending.
        saved = (await context.Client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{item.Id}/acquisition"))!;
        context.Store.FailPublication = true;
        var requestId = Guid.NewGuid();
        using var pending = await AcquisitionDocumentEndpointTests.UploadAsync(context.Client, path, saved, requestId, PhotoFixture.Png());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        Assert.Equal("Pending", (await context.Client.GetFromJsonAsync<AcquisitionDocumentOperationResponse>($"{path}/operations/{requestId}"))!.State);
        var reads = context.Store.Reads;

        // WHEN preparing a package THEN only the linked acquisition appears with explicit empty paperwork.
        using var response = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var text = await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json");
        using var manifest = JsonDocument.Parse(text);
        var acquisition = Assert.Single(manifest.RootElement.GetProperty("acquisitions").EnumerateArray());
        Assert.Equal(saved.Acquisition!.Id, acquisition.GetProperty("acquisition_id").GetGuid());
        Assert.Empty(acquisition.GetProperty("documents").EnumerateArray());
        Assert.Equal(0, manifest.RootElement.GetProperty("document_count").GetInt32());
        Assert.DoesNotContain(orphan.Acquisition.Id.ToString("D"), text);
        Assert.DoesNotContain(orphanDocument.Id.ToString("D"), text);
        Assert.DoesNotContain(removed.Id.ToString("D"), text);
        Assert.DoesNotContain(requestId.ToString("D"), text);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("documents/", StringComparison.Ordinal));
        Assert.Equal(reads, context.Store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentRenameOrRemovalAfterSnapshotPreservesCapturedMetadataAndBytes(bool remove)
    {
        // GIVEN a package paused at provider IO after metadata capture and an independent writer.
        await using var context = await Context.CreateAsync(sqlServer);
        var item = await CreateItemAsync(context.Client);
        var saved = await context.CreateAcquisitionAsync(item);
        var document = await context.UploadAsync(item.Id);
        using var writer = context.Factory.CreateClient();
        await LoginAsync(writer);
        var path = $"/api/items/{item.Id}/acquisition/{saved.Acquisition!.Id}/documents";
        var listing = (await writer.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
        context.Store.Pause = true;
        var exporting = context.ExportAsync();
        try
        {
            await context.Store.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // WHEN changing the document while captured bytes are being read.
            using var changed = await SendAsync(writer, remove ? HttpMethod.Delete : HttpMethod.Put, $"{path}/{document.Id}",
                new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), listing.ItemVersion, listing.AcquisitionVersion, document.Version, remove ? null : "Renamed"))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        finally { context.Store.Release.TrySetResult(); }
        // THEN writes commit without SQL locks and the completed package retains the old coherent snapshot.
        using var response = await exporting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        var old = Assert.Single(Assert.Single(manifest.RootElement.GetProperty("acquisitions").EnumerateArray()).GetProperty("documents").EnumerateArray());
        Assert.Equal("Receipt", old.GetProperty("label").GetString());
        using var bytes = new MemoryStream();
        await zip.GetEntry(old.GetProperty("path").GetString()!)!.Open().CopyToAsync(bytes);
        Assert.Equal(PhotoFixture.Png(), bytes.ToArray());
        // AND the next request observes current metadata and excludes removed documents.
        using var next = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var nextZip = new ZipArchive(new MemoryStream(await next.Content.ReadAsByteArrayAsync()));
        using var nextManifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(nextZip, "manifest.json"));
        var documents = Assert.Single(nextManifest.RootElement.GetProperty("acquisitions").EnumerateArray()).GetProperty("documents").EnumerateArray().ToArray();
        if (remove) { Assert.Empty(documents); Assert.DoesNotContain(nextZip.Entries, entry => entry.FullName.StartsWith("documents/", StringComparison.Ordinal)); }
        else Assert.Equal("Renamed", Assert.Single(documents).GetProperty("label").GetString());
    }

    private static string Decode(string value) => value.StartsWith('\'') ? value[1..] : value;

    private sealed class Context(AuthTestApplication application, ControlledStore store, WebApplicationFactory<Program> factory, HttpClient client) : IAsyncDisposable
    {
        public AuthTestApplication Application { get; } = application;
        public ControlledStore Store { get; } = store;
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = client;
        public static async Task<Context> CreateAsync(SqlServerFixture sqlServer)
        {
            var application = await AuthTestApplication.CreateAsync(sqlServer);
            var store = new ControlledStore();
            var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(store);
            }));
            var client = factory.CreateClient();
            await LoginAsync(client);
            return new(application, store, factory, client);
        }
        public Task<HttpResponseMessage> ExportAsync(bool package = true, string scope = "all") =>
            SendAsync(Client, HttpMethod.Post, package ? "/api/items/export-package" : "/api/items/export", new { scope });
        public async Task<ItemAcquisitionResponse> CreateAcquisitionAsync(ItemDetailResponse item, string method = "Unknown", string? source = null,
            int? year = null, int? month = null, int? day = null, string? notes = null)
        {
            using var response = await SendAsync(Client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition",
                new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, method, source, year, month, day, notes));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        }
        public async Task<AcquisitionDocumentResponse> UploadAsync(Guid item, string label = "Receipt")
        {
            var acquisition = (await Client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{item}/acquisition"))!;
            var path = $"/api/items/{item}/acquisition/{acquisition.Acquisition!.Id}/documents";
            using var response = await AcquisitionDocumentEndpointTests.UploadAsync(Client, path, acquisition, Guid.NewGuid(), PhotoFixture.Png(), label);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.Single((await Client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents);
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult();
            Client.Dispose();
            await Factory.DisposeAsync();
            await Application.DisposeAsync();
            Store.Dispose();
        }
    }

    private sealed class ControlledStore : IBlobStore, IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-acquisition-export-" + Guid.NewGuid().ToString("N"));
        private readonly FileSystemBlobStore inner;
        public ControlledStore() { Directory.CreateDirectory(root); inner = new(root); }
        public string Alias => inner.Alias;
        public string? Failure { get; set; }
        public bool FailPublication { get; set; }
        public bool Pause { get; set; }
        public int Reads { get; private set; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken) => inner.StageAsync(id, content, maximumBytes, cancellationToken);
        public async Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(id, cancellationToken);
            if (FailPublication) throw new IOException("Injected lost publication response.");
        }
        public async Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            Reads++;
            if (Pause) { Pause = false; Held.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            if (Failure == "missing") throw new FileNotFoundException("Injected private missing object.");
            if (Failure == "unreadable") throw new IOException("Injected private read failure.");
            var stream = await inner.OpenReadAsync(id, cancellationToken);
            if (Failure is null) return stream;
            await using (stream)
            {
                using var copied = new MemoryStream();
                await stream.CopyToAsync(copied, cancellationToken);
                var bytes = copied.ToArray();
                if (Failure == "truncated") bytes = bytes[..^1]; else bytes[^1] ^= 1;
                return new MemoryStream(bytes, writable: false);
            }
        }
        public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken) => inner.DeleteAsync(id, cancellationToken);
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);
        public Task CheckReadyAsync(CancellationToken cancellationToken) => inner.CheckReadyAsync(cancellationToken);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
