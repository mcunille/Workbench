// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed class AccountingAccount
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public Guid Version { get; set; }
}
public sealed class AccountingConfigurationRow
{
    public Guid TenantId { get; set; }
    public string Payload { get; set; } = "";
    public Guid Version { get; set; }
}
public sealed class AccountingRevision
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string Operation { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid Version { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
public sealed class AccountingReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string Operation { get; set; } = "";
    public Guid? AccountId { get; set; }
    public Guid? ExpectedVersion { get; set; }
    public string Payload { get; set; } = "";
    public Guid SavedVersion { get; set; }
    public string AccountIdsJson { get; set; } = "[]";
    public DateTimeOffset RecordedAtUtc { get; set; }
}
