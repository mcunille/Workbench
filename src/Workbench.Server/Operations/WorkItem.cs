// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Operations;

public enum WorkKind { DeleteAttachment = 1, DeliverIdentityMessage = 2 }
public enum WorkState { Ready, Leased, Completed, Dead }

public sealed class WorkItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public WorkKind Kind { get; set; }
    public Guid? AttachmentId { get; set; }
    public Guid? IdentityOperationId { get; set; }
    public byte[]? ProtectedPayload { get; set; }
    public WorkState State { get; set; }
    public int Attempts { get; set; }
    public long Generation { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public string? Outcome { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
