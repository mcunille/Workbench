// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Tenancy;
namespace Workbench.Server.Purchasing;

public sealed class DraftOrderRequestReceipt : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; init; }
    public Guid DraftOrderId { get; init; }
    public required string Operation { get; init; }
    public Guid ActorUserId { get; init; }
    public byte[]? ExpectedRowVersion { get; init; }
    public short FingerprintVersion { get; init; } = 1;
    public required byte[] InputFingerprint { get; init; }
    public required byte[] ResultRowVersion { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
