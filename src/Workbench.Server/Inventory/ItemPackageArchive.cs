// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

public sealed record PackagePhoto(Guid RevisionId, string ProviderAlias, long Length, string Sha256);
public sealed record PackageItem(ExportItem Record, PackagePhoto? Photo);

public static class ItemPackageArchive
{
    public const int MaximumBytes = 128 * 1024 * 1024;
    public const int MaximumManifestBytes = 16 * 1024 * 1024;
    public const int MaximumPhotoBytes = 10 * 1024 * 1024;
    public const int MaximumDocuments = 10_000;

    public static async Task<byte[]> EncodeAsync(IReadOnlyList<PackageItem> items, Guid tenantId, string scope,
        DateTimeOffset exportedAt, IBlobStore store, CancellationToken cancellationToken,
        IReadOnlyList<PackageDocument>? documents = null)
    {
        documents ??= [];
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count > ItemExportCsv.MaximumRows) throw new ItemExportLimitException();
        if (documents.Count > MaximumDocuments) throw new ItemExportLimitException();
        var acquisitions = items.Where(item => item.Record.Acquisition is not null)
            .GroupBy(item => item.Record.Acquisition!.Id).ToArray();
        var acquisitionIds = acquisitions.Select(group => group.Key).ToHashSet();
        var documentGroups = documents.ToLookup(document => document.AcquisitionId);
        var documentIds = new HashSet<Guid>();
        long photoBytes = 0;
        foreach (var item in items)
        {
            if (item.Photo is not { } photo) continue;
            if (photo.Length > MaximumPhotoBytes) throw new ItemExportLimitException();
            if (photo.Length <= 0 || photo.ProviderAlias != store.Alias || photo.Sha256.Length != 64 ||
                photo.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new IOException("Required photograph metadata is unavailable.");
            photoBytes += photo.Length;
            if (photoBytes > MaximumBytes) throw new ItemExportLimitException();
        }
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Length > DocumentValidator.MaximumBytes) throw new ItemExportLimitException();
            if (!acquisitionIds.Contains(document.AcquisitionId) || !documentIds.Add(document.Id) ||
                !ItemPackageDocuments.ValidType(document) || document.Length <= 0 || document.ProviderAlias != store.Alias ||
                document.Sha256.Length != 64 || document.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new IOException("Required document metadata is unavailable.");
            photoBytes += document.Length;
            if (photoBytes > MaximumBytes) throw new ItemExportLimitException();
        }
        var csv = ItemExportCsv.Encode(items.Select(item => item.Record).ToArray(), scope, exportedAt, cancellationToken);
        var manifest = Manifest(items, acquisitions, documentGroups, documents.Count, scope, exportedAt, cancellationToken);
        var readme = Encoding.UTF8.GetBytes($"""
            Workbench collection package version 2
            Scope: {scope}; exported at UTC: {Timestamp(exportedAt)}

            Extract this ZIP using ordinary local tools. records.csv contains CSV version 2;
            manifest.json contains literal names and locations and one entry for every item.
            Match item_id across CSV, manifest and photos/<item_id>.webp. Open photographs with
            a WebP-capable image viewer. These are exact stored detail photographs, not camera
            originals. Thumbnails and retired photographs are excluded.
            Photo status 'none' means no photograph existed in the snapshot. 'included' provides
            path, image/webp media type, byte_length and SHA-256 digest of the included bytes.
            A required photograph retrieval failure prevents the entire package from succeeding.

            Each item's acquisition_id is null when no acquisition is recorded. Match it to the
            acquisitions array, which includes each shared acquisition once. included_item_ids
            lists only records in this export scope, not the acquisition's complete membership.
            Shared documents may describe pieces outside the chosen scope; their contents and
            collector-written notes are not redacted. No excluded item identities are added.
            Documents are included once under documents/<acquisition_id>/<document_id>.<extension>.
            Match their document_id, label, path, media_type, byte_length and sha256 in the manifest.
            Open PDFs and images with ordinary local viewers. These are exact stored files and may
            contain embedded metadata. Stored paperwork is not independently verified provenance.
            An empty documents array means none existed in the snapshot. Missing, corrupt or
            unreadable required documents fail the whole package rather than becoming absent.
            Unknown source and date components are null in JSON and empty in CSV. Date precision
            is unknown, year, month or day; absent components never imply January 1.

            CSV uses UTF-8 with BOM, comma delimiter, CRLF separators and quoted fields.
            Import columns as Text in spreadsheets. Each present name, notes and location has
            exactly one added ASCII apostrophe for formula safety, as do acquisition_source and
            acquisition_notes. After CSV parsing, remove
            exactly one leading apostrophe to recover the original text; empty optional fields
            mean absent. Quoted newlines and Unicode remain data. Re-saving or removing prefixes
            may remove formula protection. Manifest JSON text needs no apostrophe decoding.
            System timestamps are UTC; IDs are stable UUIDs. Scope 'active' excludes archived
            records; 'all' includes them. Archive is not sale, disposal or ownership status.

            This is a collection copy, not a backup. It cannot restore Workbench and is not an
            import facility. Edit history, replay payloads, credentials and sessions are excluded.
            """);
        if (photoBytes + csv.LongLength + manifest.LongLength + readme.LongLength > MaximumBytes)
            throw new ItemExportLimitException();
        using var output = new PackageBuffer(MaximumBytes);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync("README.txt", readme);
            await WriteAsync("records.csv", csv);
            await WriteAsync("manifest.json", manifest);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Photo is not { } photo) continue;
                // Only immutable, tenant-owned revision metadata captured by the export transaction
                // reaches here. Current-photo lookup would race with replacement and removal.
                await using var source = BlobIntegrity.Open(await store.OpenReadAsync(
                    new BlobObjectId(tenantId, photo.RevisionId), cancellationToken), new(photo.Length, photo.Sha256));
                await using var destination = archive.CreateEntry(PhotoPath(item.Record.Id), CompressionLevel.NoCompression).Open();
                await BlobTransfer.CopyAsync(source, destination, photo.Length, cancellationToken);
            }
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var source = BlobIntegrity.Open(await store.OpenReadAsync(
                    new BlobObjectId(tenantId, document.RevisionId), cancellationToken), new(document.Length, document.Sha256));
                await using var destination = archive.CreateEntry(document.Path, CompressionLevel.NoCompression).Open();
                await BlobTransfer.CopyAsync(source, destination, document.Length, cancellationToken);
            }

            async Task WriteAsync(string path, byte[] bytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var destination = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
                await destination.WriteAsync(bytes, cancellationToken);
            }
        }
        // Includes final central-directory overhead, not just entry content.
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static byte[] Manifest(IReadOnlyList<PackageItem> items, IGrouping<Guid, PackageItem>[] acquisitions,
        ILookup<Guid, PackageDocument> documents, int documentCount, string scope, DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        using var output = new PackageBuffer(MaximumManifestBytes);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("package_version", 2);
        writer.WriteString("scope", scope);
        writer.WriteString("exported_at_utc", Timestamp(exportedAt));
        writer.WriteNumber("record_count", items.Count);
        writer.WriteNumber("photo_count", items.Count(item => item.Photo is not null));
        writer.WriteNumber("acquisition_count", acquisitions.Length);
        writer.WriteNumber("document_count", documentCount);
        writer.WriteStartArray("items");
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("item_id", item.Record.Id);
            writer.WriteString("name", item.Record.Name);
            writer.WriteString("location", item.Record.Location);
            if (item.Record.Acquisition is { } acquisition) writer.WriteString("acquisition_id", acquisition.Id);
            else writer.WriteNull("acquisition_id");
            writer.WriteStartObject("photo");
            writer.WriteString("status", item.Photo is null ? "none" : "included");
            if (item.Photo is { } photo)
            {
                writer.WriteString("path", PhotoPath(item.Record.Id));
                writer.WriteString("media_type", "image/webp");
                writer.WriteNumber("byte_length", photo.Length);
                writer.WriteString("sha256", photo.Sha256);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush(); // Bound the serializer's staging allocation to one durable item.
        }
        writer.WriteEndArray();
        writer.WriteStartArray("acquisitions");
        foreach (var group in acquisitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquisition = group.First().Record.Acquisition!;
            writer.WriteStartObject();
            writer.WriteString("acquisition_id", acquisition.Id);
            writer.WriteString("method", acquisition.Method);
            writer.WriteString("source", acquisition.Source);
            writer.WriteString("date_precision", acquisition.DatePrecision);
            WriteDatePart("year", acquisition.Year);
            WriteDatePart("month", acquisition.Month);
            WriteDatePart("day", acquisition.Day);
            writer.WriteString("notes", acquisition.Notes);
            writer.WriteStartArray("included_item_ids");
            foreach (var item in group) writer.WriteStringValue(item.Record.Id);
            writer.WriteEndArray();
            writer.WriteStartArray("documents");
            foreach (var document in documents[acquisition.Id])
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("document_id", document.Id);
                writer.WriteString("label", document.Label);
                writer.WriteString("created_at_utc", Timestamp(document.CreatedAtUtc));
                writer.WriteString("path", document.Path);
                writer.WriteString("media_type", document.MediaType);
                writer.WriteNumber("byte_length", document.Length);
                writer.WriteString("sha256", document.Sha256);
                writer.WriteEndObject();
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return output.ToArray();

        void WriteDatePart(string name, int? value)
        {
            if (value is { } number) writer.WriteNumber(name, number);
            else writer.WriteNull(name);
        }
    }

    private static string PhotoPath(Guid id) => $"photos/{id:D}.webp";
    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
