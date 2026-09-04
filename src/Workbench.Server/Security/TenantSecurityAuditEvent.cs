// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Security;

public sealed class TenantSecurityAuditEvent : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Action { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public string Outcome { get; set; } = "Succeeded";

    public string? CorrelationId { get; set; }

    public string? MetadataJson { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
