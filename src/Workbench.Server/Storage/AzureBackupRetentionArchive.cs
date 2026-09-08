// Copyright (c) 2026 The White Stag Collection.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text.Json;

namespace Workbench.Server.Storage;

// This adapter belongs only to the retention identity; the collector has no Delete operation.
public sealed class AzureBackupRetentionArchive(BlobContainerClient container) : IBackupRetentionArchive
{
    public async Task<IReadOnlyList<RetentionObject>> ListAsync(Guid installation, CancellationToken cancellationToken)
    {
        var result = new List<RetentionObject>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{installation:N}/", cancellationToken))
            result.Add(new(item.Name, item.Properties.ETag?.ToString() ?? "", item.Properties.CreatedOn ?? default, item.Properties.LastModified ?? default));
        return result;
    }
    public async Task<BackupCatalog> ReadCatalogAsync(RetentionObject item, CancellationToken cancellationToken)
    {
        var response = await container.GetBlobClient(item.Name).DownloadStreamingAsync(new BlobDownloadOptions
        { Conditions = new BlobRequestConditions { IfMatch = new ETag(item.ETag) } }, cancellationToken);
        await using var stream = response.Value.Content;
        return await JsonSerializer.DeserializeAsync<BackupCatalog>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Invalid backup catalog.");
    }
    public async Task DeleteAsync(RetentionObject item, CancellationToken cancellationToken)
    {
        try
        {
            // Azure enforces any extended immutability interval or legal hold. Never weaken either.
            await container.GetBlobClient(item.Name).DeleteAsync(conditions: new BlobRequestConditions { IfMatch = new ETag(item.ETag) }, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException error) when (error.Status == 404 && error.ErrorCode == "BlobNotFound")
        {
            // A concurrent successful expiration already made this exact object absent.
        }
    }
}
