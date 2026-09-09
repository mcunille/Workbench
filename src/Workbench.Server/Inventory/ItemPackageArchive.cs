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

    public static async Task<byte[]> EncodeAsync(IReadOnlyList<PackageItem> items, Guid tenantId, string scope,
        DateTimeOffset exportedAt, IBlobStore store, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count > ItemExportCsv.MaximumRows) throw new ItemExportLimitException();
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
        var csv = ItemExportCsv.Encode(items.Select(item => item.Record).ToArray(), scope, exportedAt, cancellationToken);
        var manifest = Manifest(items, scope, exportedAt, cancellationToken);
        var readme = Encoding.UTF8.GetBytes($"""
            Workbench collection package version 1
            Scope: {scope}; exported at UTC: {Timestamp(exportedAt)}

            Extract this ZIP using ordinary local tools. records.csv contains CSV version 1;
            manifest.json contains literal names and locations and one entry for every item.
            Match item_id across CSV, manifest and photos/<item_id>.webp. Open photographs with
            a WebP-capable image viewer. These are exact stored detail photographs, not camera
            originals. Thumbnails and retired photographs are excluded.
            Photo status 'none' means no photograph existed in the snapshot. 'included' provides
            path, image/webp media type, byte_length and SHA-256 digest of the included bytes.
            A required photograph retrieval failure prevents the entire package from succeeding.

            CSV uses UTF-8 with BOM, comma delimiter, CRLF separators and quoted fields.
            Import columns as Text in spreadsheets. Each present name, notes and location has
            exactly one added ASCII apostrophe for formula safety. After CSV parsing, remove
            exactly one leading apostrophe to recover the original text; empty optional fields
            mean absent. Quoted newlines and Unicode remain data. Re-saving or removing prefixes
            may remove formula protection. Manifest JSON text needs no apostrophe decoding.
            System timestamps are UTC; IDs are stable UUIDs. Scope 'active' excludes archived
            records; 'all' includes them. Archive is not sale, disposal or ownership status.

            This is a collection copy, not a backup. It cannot restore Workbench and is not an
            import facility. History, replay payloads, credentials and sessions are excluded.
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

    private static byte[] Manifest(IReadOnlyList<PackageItem> items, string scope, DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        using var output = new PackageBuffer(MaximumManifestBytes);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("package_version", 1);
        writer.WriteString("scope", scope);
        writer.WriteString("exported_at_utc", Timestamp(exportedAt));
        writer.WriteNumber("record_count", items.Count);
        writer.WriteNumber("photo_count", items.Count(item => item.Photo is not null));
        writer.WriteStartArray("items");
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("item_id", item.Record.Id);
            writer.WriteString("name", item.Record.Name);
            writer.WriteString("location", item.Record.Location);
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
        writer.WriteEndObject();
        writer.Flush();
        return output.ToArray();
    }

    private static string PhotoPath(Guid id) => $"photos/{id:D}.webp";
    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
