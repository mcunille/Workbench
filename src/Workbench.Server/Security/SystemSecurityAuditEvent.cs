// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Security;

public sealed class SystemSecurityAuditEvent
{
    public Guid Id { get; set; }

    public required string Action { get; set; }

    public required string Outcome { get; set; }

    public string? CorrelationId { get; set; }

    public string? MetadataJson { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
