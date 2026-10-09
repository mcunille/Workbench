// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.ServiceAdministration;

public sealed class ServiceAdminAccount
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long SecurityVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
