// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Workbench.Server.Storage;

// Deliberately separate from IBlobStore: online capture requires version identities and never deletes.
public sealed class AzureBackupSource(BlobContainerClient container, Guid installationId) : IBackupVersionSource
{
    public int IgnoredObjects { get; private set; }
    public async IAsyncEnumerable<BackupVersion> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prefix = $"{installationId:N}/";
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.Version | BlobStates.Deleted,
            prefix, cancellationToken))
        {
            var parts = blob.Name[prefix.Length..].Split('/');
            if (parts.Length != 2 || parts[1].Length != 34 || !parts[1].EndsWith(".b", StringComparison.Ordinal) ||
                !Guid.TryParseExact(parts[0], "N", out var tenant) || tenant == Guid.Empty ||
                !Guid.TryParseExact(parts[1][..32], "N", out var revision) || revision == Guid.Empty)
            {
                IgnoredObjects++;
                continue;
            }
            // A missing version ID is protection drift, never permission to copy the mutable current blob.
            yield return new BackupVersion(blob.Name, blob.VersionId ?? "", blob.Properties.ETag?.ToString() ?? "",
                blob.Properties.ContentLength ?? -1, tenant, revision);
        }
    }

    public async Task<Stream> OpenAsync(BackupVersion version, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(version.Name).WithVersion(version.VersionId).DownloadStreamingAsync(
                new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = new ETag(version.ETag) } }, cancellationToken);
            return response.Value.Content;
        }
        catch (RequestFailedException error) when (error.Status == 404 && error.ErrorCode == "BlobNotFound")
        {
            throw new FileNotFoundException("Enumerated version unavailable.");
        }
    }
}

public sealed class AzureBackupArchive(BlobContainerClient container) : IBackupArchive
{
    public async Task VerifyRetainedAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{installationId:N}/", cancellationToken))
        {
            if (!blob.Name.EndsWith("/catalog.json", StringComparison.Ordinal)) continue;
            await using var input = await OpenAsync(blob.Name, cancellationToken);
            var catalog = await System.Text.Json.JsonSerializer.DeserializeAsync<BackupCatalog>(input, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Invalid retained catalog.");
            if (catalog.Version != 1 || catalog.Metadata.InstallationId != installationId ||
                blob.Name != $"{installationId:N}/{catalog.BackupId:N}/catalog.json")
                throw new InvalidDataException("Retained catalog identity differs.");
            if (catalog.ExpiresAtUtc <= DateTimeOffset.UtcNow) continue;
            foreach (var entry in catalog.Objects)
            {
                if (!entry.Destination.StartsWith($"{installationId:N}/{catalog.BackupId:N}/objects/", StringComparison.Ordinal) ||
                    entry.Length is < 0 or > 25 * 1024 * 1024)
                    throw new InvalidDataException("Invalid retained object binding.");
                await using var content = await OpenAsync(entry.Destination, cancellationToken);
                var identity = await BlobTransfer.CopyAsync(content, Stream.Null, Math.Max(1, entry.Length), cancellationToken);
                if (identity.Length != entry.Length || identity.Sha256 != entry.Sha256)
                    throw new InvalidDataException("Retained backup content is missing or corrupt.");
            }
        }
    }

    public async Task CreateAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            await container.GetBlobClient(name).UploadAsync(content,
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
        }
        catch (RequestFailedException error) when (error.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet" or "BlobImmutableDueToPolicy")
        {
            throw new BackupObjectExistsException();
        }
    }
    public async Task<Stream> OpenAsync(string name, CancellationToken cancellationToken) =>
        (await container.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: cancellationToken)).Value.Content;
}
