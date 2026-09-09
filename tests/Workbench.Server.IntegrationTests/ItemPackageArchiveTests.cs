// Copyright (c) 2026 The White Stag Collection.

using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ItemPackageArchiveTests
{
    [Fact]
    public void ArchiveBackingAllocationDoesNotGrowPastItsByteCeiling()
    {
        // GIVEN a bounded archive backing stream with a non-power-of-two first allocation.
        // Inspect capacity because file length alone cannot establish the memory resource bound.
        var type = typeof(ItemPackageArchive).Assembly.GetType("Workbench.Server.Inventory.PackageBuffer", throwOnError: true)!;
        using var buffer = (MemoryStream)Activator.CreateInstance(type, 1024)!;
        buffer.Write(new byte[700]);
        // WHEN another valid write would make ordinary MemoryStream double beyond the ceiling.
        buffer.Write(new byte[200]);
        // THEN valid content fits without an oversized backing allocation; excess content still fails.
        Assert.Equal(900, buffer.Length);
        Assert.InRange(buffer.Capacity, 900, 1024);
        buffer.Write(new byte[124]);
        Assert.Equal(1024, buffer.Length);
        Assert.Equal(1024, buffer.Capacity);
        Assert.Throws<ItemExportLimitException>(() => buffer.WriteByte(0));
    }

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
    private static readonly ExportItem Record = new(Guid.NewGuid(), "Individual", "=蓝../stone", "note", "Tray A", Timestamp, null);

    [Fact]
    public async Task PackagePreservesStoredBytesAndMapsThemToLiteralTextAndCsv()
    {
        // GIVEN immutable stored bytes and records with and without photographs.
        var store = new TestStore();
        var photo = new PackagePhoto(Guid.NewGuid(), store.Alias, store.Bytes.Length, Convert.ToHexString(SHA256.HashData(store.Bytes)));
        var absent = Record with { Id = Guid.NewGuid(), Location = null };
        var anotherAbsent = absent with { Id = Guid.NewGuid() };
        // WHEN preparing the package through the provider boundary.
        var bytes = await ItemPackageArchive.EncodeAsync([new(Record, photo), new(absent, null), new(anotherAbsent, null)], Tenant, "all", Timestamp, store, CancellationToken.None);
        // THEN exact stored bytes and stable identifiers connect CSV and literal manifest text.
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(4, zip.Entries.Count);
        using var content = new MemoryStream();
        await zip.GetEntry($"photos/{Record.Id:D}.webp")!.Open().CopyToAsync(content);
        Assert.Equal(store.Bytes, content.ToArray());
        Assert.Equal(new BlobObjectId(Tenant, photo.RevisionId), store.Opened);
        Assert.True(store.Disposed);
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("package_version").GetInt32());
        Assert.Equal(3, root.GetProperty("record_count").GetInt32());
        Assert.Equal(1, root.GetProperty("photo_count").GetInt32());
        Assert.Equal("all", root.GetProperty("scope").GetString());
        Assert.Equal(Timestamp, root.GetProperty("exported_at_utc").GetDateTimeOffset());
        var first = root.GetProperty("items")[0];
        Assert.Equal(Record.Id, first.GetProperty("item_id").GetGuid());
        Assert.Equal(Record.Name, first.GetProperty("name").GetString());
        Assert.Equal("Tray A", first.GetProperty("location").GetString());
        var included = first.GetProperty("photo");
        Assert.Equal("included", included.GetProperty("status").GetString());
        Assert.Equal($"photos/{Record.Id:D}.webp", included.GetProperty("path").GetString());
        Assert.Equal("image/webp", included.GetProperty("media_type").GetString());
        Assert.Equal(photo.Length, included.GetProperty("byte_length").GetInt64());
        Assert.Equal(photo.Sha256, included.GetProperty("sha256").GetString());
        Assert.Equal("none", root.GetProperty("items")[1].GetProperty("photo").GetProperty("status").GetString());
        Assert.Equal("none", root.GetProperty("items")[2].GetProperty("photo").GetProperty("status").GetString());
        using var csv = new MemoryStream();
        await zip.GetEntry("records.csv")!.Open().CopyToAsync(csv);
        Assert.Equal(ItemExportCsv.Encode([Record, absent, anotherAbsent], "all", Timestamp, CancellationToken.None), csv.ToArray());
        Assert.Contains("not a backup", await ItemPackageEndpointTests.ReadAsync(zip, "README.txt"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("digest")]
    [InlineData("extra")]
    [InlineData("provider")]
    public async Task RequiredPhotoFailureNeverBecomesAbsence(string failure)
    {
        // GIVEN a captured required photo whose provider or bytes no longer match authoritative metadata.
        var store = new TestStore { Missing = failure == "missing" };
        var photo = new PackagePhoto(Guid.NewGuid(), failure == "provider" ? "foreign" : store.Alias,
            store.Bytes.Length + (failure == "truncated" ? 1 : failure == "extra" ? -1 : 0),
            failure == "digest" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(store.Bytes)));
        // WHEN preparing THEN the operation fails instead of returning an incomplete successful ZIP.
        var exception = await RecordExceptionAsync();
        Assert.True(exception is IOException or InvalidDataException);

        async Task<Exception?> RecordExceptionAsync() => await Xunit.Record.ExceptionAsync(() =>
            ItemPackageArchive.EncodeAsync([new(Record, photo)], Tenant, "active", Timestamp, store, CancellationToken.None));
    }

    [Fact]
    public async Task LimitsAndCancellationRejectBeforeOpeningBlobs()
    {
        // GIVEN excess rows, excess declared photo size, or a cancelled request.
        var store = new TestStore();
        var photo = new PackagePhoto(Guid.NewGuid(), store.Alias, ItemPackageArchive.MaximumPhotoBytes + 1L, new string('0', 64));
        // WHEN preparing THEN bounds and cancellation fail without reading the provider.
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync(Enumerable.Repeat(new PackageItem(Record, null), 10001).ToArray(), Tenant, "all", Timestamp, store, CancellationToken.None));
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync([new(Record, photo)], Tenant, "all", Timestamp, store, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ItemPackageArchive.EncodeAsync([new(Record, null)], Tenant, "all", Timestamp, store, new CancellationToken(true)));
        Assert.Null(store.Opened);
    }

    [Theory]
    [InlineData(0, "valid")]
    [InlineData(-1, "valid")]
    [InlineData(4, "short")]
    [InlineData(4, "nonhex")]
    public async Task InvalidCapturedMetadataFailsBeforeProviderAccess(long length, string digest)
    {
        // GIVEN invalid captured byte length or SHA-256 metadata.
        var store = new TestStore();
        var photo = new PackagePhoto(Guid.NewGuid(), store.Alias, length,
            digest == "short" ? "abc" : new string(digest == "nonhex" ? 'g' : '0', 64));
        // WHEN encoding THEN metadata cannot be used to open an object or manufacture photo absence.
        await Assert.ThrowsAsync<IOException>(() => ItemPackageArchive.EncodeAsync([new(Record, photo)], Tenant, "all", Timestamp, store, CancellationToken.None));
        Assert.Null(store.Opened);
    }

    [Fact]
    public async Task AggregatePhotoLimitRejectsBeforeAllocatingOrReadingObjects()
    {
        // GIVEN individually valid photo sizes whose aggregate exceeds 128 MiB.
        var store = new TestStore();
        var items = Enumerable.Range(0, 13).Select(_ => new PackageItem(Record with { Id = Guid.NewGuid() },
            new PackagePhoto(Guid.NewGuid(), store.Alias, ItemPackageArchive.MaximumPhotoBytes, new string('0', 64)))).ToArray();
        // WHEN encoding THEN aggregate metadata preflight prevents all provider reads.
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync(items, Tenant, "all", Timestamp, store, CancellationToken.None));
        Assert.Null(store.Opened);
    }

    [Fact]
    public async Task AggregateLimitIncludesRecordsManifestAndReadmeBeyondPhotoBytes()
    {
        // GIVEN photo bytes alone exactly at the aggregate bound, leaving no room for required text entries.
        var store = new TestStore();
        var items = Enumerable.Range(0, 13).Select(index => new PackageItem(Record with { Id = Guid.NewGuid() },
            new PackagePhoto(Guid.NewGuid(), store.Alias,
                index == 12 ? 8 * 1024 * 1024 : ItemPackageArchive.MaximumPhotoBytes, new string('0', 64)))).ToArray();
        Assert.Equal(ItemPackageArchive.MaximumBytes, items.Sum(item => item.Photo!.Length));
        // WHEN encoding THEN mandatory text metadata is counted before reading any large object.
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync(items, Tenant, "all", Timestamp, store, CancellationToken.None));
        Assert.Null(store.Opened);
    }

    [Fact]
    public async Task ManifestBoundCountsJsonEscapingIndependentlyOfCsvBytes()
    {
        // GIVEN domain-length Unicode names/locations that fit CSV but exceed 16 MiB once JSON escapes them.
        var store = new TestStore();
        var record = Record with { Name = new string('蓝', 200), Location = new string('蓝', 200), Notes = null };
        var items = Enumerable.Repeat(new PackageItem(record, null), 8000).ToArray();
        var csv = ItemExportCsv.Encode(items.Select(item => item.Record).ToArray(), "all", Timestamp, CancellationToken.None);
        Assert.True(csv.Length < ItemExportCsv.MaximumBytes);
        Assert.True(8000L * 400 * 6 > ItemPackageArchive.MaximumManifestBytes);
        // WHEN encoding THEN manifest overflow fails independently of row and CSV limits.
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync(items, Tenant, "all", Timestamp, store, CancellationToken.None));
        Assert.Null(store.Opened);
    }

    [Fact]
    public async Task ExactPerPhotoBoundarySucceedsAndDisposesTheProviderStream()
    {
        // GIVEN one immutable photograph exactly at the ten-MiB boundary.
        var store = new TestStore { Bytes = new byte[ItemPackageArchive.MaximumPhotoBytes] };
        var photo = new PackagePhoto(Guid.NewGuid(), store.Alias, store.Bytes.Length, Convert.ToHexString(SHA256.HashData(store.Bytes)));
        // WHEN encoding THEN the boundary photograph is included completely and its source is closed.
        var bytes = await ItemPackageArchive.EncodeAsync([new(Record, photo)], Tenant, "all", Timestamp, store, CancellationToken.None);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(photo.Length, zip.GetEntry($"photos/{Record.Id:D}.webp")!.Length);
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task FinalCentralDirectoryCannotExceedThePackageBoundAfterAllPhotosWereCopied()
    {
        // GIVEN a small real package used to measure metadata and ZIP overhead for thirteen fixed entry names.
        var store = new TestStore { Bytes = [0] };
        var items = Enumerable.Range(0, 13).Select(_ => new PackageItem(Record with { Id = Guid.NewGuid() },
            new PackagePhoto(Guid.NewGuid(), store.Alias, 1, Convert.ToHexString(SHA256.HashData(store.Bytes))))).ToArray();
        var sample = await ItemPackageArchive.EncodeAsync(items, Tenant, "all", Timestamp, store, CancellationToken.None);
        using var sampleZip = new ZipArchive(new MemoryStream(sample));
        var metadataLength = sampleZip.Entries.Where(entry => !entry.FullName.StartsWith("photos/", StringComparison.Ordinal)).Sum(entry => entry.Length);
        var overhead = sample.LongLength - metadataLength - items.Length;
        var centralLength = BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(sample.Length - 10, 4));
        Assert.True(centralLength > 0 && centralLength < overhead);

        // AND entry content plus local headers fits, but adding the final central directory does not.
        // Twelve photo sizes gain seven decimal digits; the last (~8 MiB) gains six in the manifest.
        var targetContentLength = ItemPackageArchive.MaximumBytes - overhead + centralLength / 2;
        var lastLength = checked((int)(targetContentLength - metadataLength - 90 - 12L * ItemPackageArchive.MaximumPhotoBytes));
        Assert.InRange(lastLength, 1_000_000, 9_999_999);
        var zeros = new byte[ItemPackageArchive.MaximumPhotoBytes];
        var lengths = new Dictionary<Guid, int>();
        for (var index = 0; index < items.Length; index++)
        {
            var length = index == 12 ? lastLength : zeros.Length;
            var photo = items[index].Photo! with { Length = length, Sha256 = Convert.ToHexString(SHA256.HashData(zeros.AsSpan(0, length))) };
            items[index] = items[index] with { Photo = photo };
            lengths.Add(photo.RevisionId, length);
        }
        Assert.True(targetContentLength <= ItemPackageArchive.MaximumBytes);
        Assert.True(targetContentLength + overhead - centralLength < ItemPackageArchive.MaximumBytes);
        Assert.True(targetContentLength + overhead > ItemPackageArchive.MaximumBytes);
        var disposed = 0;
        var opened = 0;
        store = new TestStore
        {
            ReadFactory = id =>
            {
                opened++;
                return new ObservedSliceStream(zeros, lengths[id.RevisionId], () => disposed++);
            },
        };

        // WHEN all valid immutable photos are copied THEN archive finalization rejects excess directory bytes.
        await Assert.ThrowsAsync<ItemExportLimitException>(() => ItemPackageArchive.EncodeAsync(items, Tenant, "all", Timestamp, store, CancellationToken.None));
        Assert.Equal(13, opened);
        Assert.Equal(13, disposed);
    }

    [Fact]
    public async Task CancellationDuringCopyClosesTheSourceAndReturnsNoArchive()
    {
        // GIVEN cancellation triggered by the provider after copying begins.
        using var cancellation = new CancellationTokenSource();
        var bytes = new byte[256 * 1024];
        var disposed = false;
        var store = new TestStore { ReadFactory = _ => new CancellingStream(bytes, cancellation, () => disposed = true) };
        var photo = new PackagePhoto(Guid.NewGuid(), store.Alias, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        // WHEN reading the photograph THEN cancellation propagates and the source is disposed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ItemPackageArchive.EncodeAsync([new(Record, photo)], Tenant, "all", Timestamp, store, cancellation.Token));
        Assert.NotNull(store.Opened);
        Assert.True(disposed);
    }

    private sealed class TestStore : IBlobStore
    {
        public string Alias => "test";
        public byte[] Bytes { get; init; } = [1, 2, 3, 4];
        public Func<BlobObjectId, Stream>? ReadFactory { get; init; }
        public bool Missing { get; init; }
        public BlobObjectId? Opened { get; private set; }
        public bool Disposed { get; private set; }
        public Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            Opened = id;
            if (Missing) throw new FileNotFoundException();
            if (ReadFactory is not null) return Task.FromResult(ReadFactory(id));
            return Task.FromResult<Stream>(new ObservedStream(Bytes, () => Disposed = true));
        }
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CheckReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ObservedStream(byte[] bytes, Action disposed) : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }

    private sealed class ObservedSliceStream(byte[] bytes, int length, Action disposed) : MemoryStream(bytes, 0, length, writable: false)
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation, Action disposed) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
}
