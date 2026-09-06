// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Storage;

public enum RevisionState { Pending, Available, Failed, Purged }

public sealed class AttachmentRevision : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AttachmentId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? PreviousRevisionId { get; set; }
    public string ProviderAlias { get; set; } = "";
    public string Source { get; set; } = "ApplicationGenerated";
    public string MediaType { get; set; } = "application/octet-stream";
    public long? Length { get; set; }
    public string? Sha256 { get; set; }
    public RevisionState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
