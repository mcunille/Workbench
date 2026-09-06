// Copyright (c) 2026 The White Stag Collection.

using Azure.Storage.Blobs;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Runtime.CompilerServices;

namespace Workbench.Server.Storage;

public sealed class AzureBlobStore(BlobContainerClient container, Guid installationId, string alias = "azure") : IBlobStore
{
    public string Alias => alias;
    public async Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            if (await Client(id, 'b').ExistsAsync(cancellationToken) || await Client(id, 'a').ExistsAsync(cancellationToken))
            {
                throw new IOException("Storage object already exists.");
            }
            var blockBlob = container.GetBlockBlobClient(Client(id, 'a').Name);
            await using var destination = new AzureBlockWriter(blockBlob);
            var identity = await BlobTransfer.CopyAsync(content, destination, maximumBytes, cancellationToken);
            await destination.CommitAsync(cancellationToken);
            return identity;
        }
        catch (RequestFailedException error)
        {
            throw Normalize(error);
        }
    }

    public async Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        try
        {
            if (!await Client(id, 'b').ExistsAsync(cancellationToken))
            {
                await CopyCreateOnlyAsync(Client(id, 'a'), Client(id, 'b'), cancellationToken);
            }
            await Client(id, 'a').DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException error)
        {
            throw Normalize(error);
        }
    }

    public async Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        try
        {
            // DownloadStreaming establishes existence before returning a stream.
            var response = await Client(id, 'b').DownloadStreamingAsync(cancellationToken: cancellationToken);
            return response.Value.Content;
        }
        catch (RequestFailedException error)
        {
            throw Normalize(error);
        }
    }

    public async Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var suffix in new[] { 'a', 'b', 'c' })
            {
                await Client(id, suffix).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }
        catch (RequestFailedException error)
        {
            throw Normalize(error);
        }
    }

    public async IAsyncEnumerable<BlobObjectId> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, Prefix, cancellationToken).AsPages(pageSizeHint: 500))
        {
            foreach (var blob in page.Values)
            {
                var parts = blob.Name[Prefix.Length..].Split('/');
                if (parts.Length == 2 && parts[1].Length == 34 && parts[1][32] == '.' && parts[1][33] is 'a' or 'b' or 'c' &&
                    Guid.TryParseExact(parts[0], "N", out var tenant) && tenant != Guid.Empty &&
                    Guid.TryParseExact(parts[1][..32], "N", out var revision) && revision != Guid.Empty)
                {
                    yield return new BlobObjectId(tenant, revision);
                }
            }
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var properties = await container.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.PublicAccess != PublicAccessType.None)
            {
                throw new IOException("Blob storage must be private.");
            }
        }
        catch (RequestFailedException error)
        {
            throw Normalize(error);
        }
    }

    private string Prefix => installationId != Guid.Empty ? $"{installationId:N}/" :
        throw new InvalidOperationException("A storage installation identifier is required.");

    private BlobClient Client(BlobObjectId id, char suffix) =>
        container.GetBlobClient($"{Prefix}{id.TenantId:N}/{id.RevisionId:N}.{suffix}");

    private static async Task CopyCreateOnlyAsync(BlobClient source, BlobClient destination, CancellationToken cancellationToken)
    {
        var download = await source.DownloadStreamingAsync(cancellationToken: cancellationToken);
        await using var stream = download.Value.Content;
        await destination.UploadAsync(stream, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            TransferOptions = new Azure.Storage.StorageTransferOptions { MaximumConcurrency = 1, MaximumTransferSize = 1024 * 1024 },
        }, cancellationToken);
    }

    private static IOException Normalize(RequestFailedException error) => error.Status == 404
        ? new FileNotFoundException("Storage object is missing.")
        : error.Status is 0 or 408 or 429 or >= 500 ? new Operations.DependencyUnavailableException()
        : new IOException("Blob provider operation failed.");
}

// Uncommitted blocks are invisible and expire under the provider's garbage collection.
// Each attempt uses distinct fixed-length block IDs; only a conditional block-list
// commit creates the staged object. A failed attempt never deletes another attempt.
internal sealed class AzureBlockWriter(BlockBlobClient client) : Stream
{
    private readonly string _attempt = Guid.NewGuid().ToString("N");
    private readonly List<string> _blocks = [];
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var block = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_attempt}-{_blocks.Count:D8}"));
        using var content = new MemoryStream(buffer.ToArray(), writable: false);
        await client.StageBlockAsync(block, content, cancellationToken: cancellationToken);
        _blocks.Add(block);
    }

    public Task CommitAsync(CancellationToken cancellationToken) => client.CommitBlockListAsync(_blocks,
        new CommitBlockListOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
}
