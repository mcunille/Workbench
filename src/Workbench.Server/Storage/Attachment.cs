// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Storage;

public sealed class Attachment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? CurrentRevisionId { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public DateTimeOffset? DeleteAfterUtc { get; set; }
    public bool Held { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
