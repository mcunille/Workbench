// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Storage;

public sealed record BlobObjectId
{
    public BlobObjectId(Guid tenantId, Guid revisionId)
    {
        if (tenantId == Guid.Empty || revisionId == Guid.Empty)
        {
            throw new ArgumentException("Nonempty storage identifiers are required.");
        }
        TenantId = tenantId;
        RevisionId = revisionId;
    }

    public Guid TenantId { get; }
    public Guid RevisionId { get; }
}

public sealed record BlobContentIdentity(long Length, string Sha256);

public interface IBlobStore
{
    string Alias { get; }
    Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken);
    Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken);
    Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken);
    IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken cancellationToken);
    Task CheckReadyAsync(CancellationToken cancellationToken);
}
