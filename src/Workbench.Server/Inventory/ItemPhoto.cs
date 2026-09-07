// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Inventory;

public sealed class ItemPhoto : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ItemId { get; set; }
    public Guid OperationId { get; set; }
    public Guid DetailAttachmentId { get; set; }
    public Guid ThumbnailAttachmentId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum PhotoOperationKind { Upload, Remove }
public enum PhotoOperationState { Pending, Completed, Conflict }

public sealed class ItemPhotoOperation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ItemId { get; set; }
    public Guid RequestId { get; set; }
    public byte[] ExpectedVersion { get; set; } = [];
    public string PayloadSha256 { get; set; } = "";
    public PhotoOperationKind Kind { get; set; }
    public PhotoOperationState State { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? DetailAttachmentId { get; set; }
    public Guid? ThumbnailAttachmentId { get; set; }
    public byte[]? ResultVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
