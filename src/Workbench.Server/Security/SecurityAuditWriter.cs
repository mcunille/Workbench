// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Workbench.Server.Persistence;

namespace Workbench.Server.Security;

public sealed class SecurityAuditWriter(WorkbenchDbContext database, TimeProvider timeProvider)
{
    private static readonly string[] ForbiddenMetadataTerms =
        ["password", "token", "secret", "credential", "connection"];

    public void AppendTenant(
        Guid tenantId,
        Guid? actorUserId,
        string action,
        string targetType,
        Guid? targetId,
        string outcome,
        string correlationId,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (metadata?.Keys.Any(key => ForbiddenMetadataTerms.Any(
            term => key.Contains(term, StringComparison.OrdinalIgnoreCase))) is true)
        {
            throw new ArgumentException("Audit metadata contains a prohibited field name.", nameof(metadata));
        }

        database.TenantSecurityAuditEvents.Add(new TenantSecurityAuditEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Outcome = outcome,
            CorrelationId = correlationId,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
            OccurredAtUtc = timeProvider.GetUtcNow(),
        });
    }
}
