// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Tenancy;
namespace Workbench.Server.Purchasing;

public sealed class PurchaseOrderDocument : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid AttachmentId { get; set; }
    public Guid RevisionId { get; set; }
    public string Label { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Extension { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class PurchaseOrderDocumentOperation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid RequestId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? AttachmentId { get; set; }
    public Guid? RevisionId { get; set; }
    public Guid ActorUserId { get; set; }
    public int Kind { get; set; }
    public int State { get; set; }
    public string? Label { get; set; }
    public string? MediaType { get; set; }
    public string? Extension { get; set; }
    public long? Length { get; set; }
    public string? Sha256 { get; set; }
    public byte[] ExpectedOrderVersion { get; set; } = [];
    public byte[]? ExpectedDocumentVersion { get; set; }
    public byte[]? ResultOrderVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
