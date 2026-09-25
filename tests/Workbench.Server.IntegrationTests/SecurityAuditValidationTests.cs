// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SecurityAuditValidationTests
{
    [Fact]
    public void AuditWriterRejectsSensitiveMetadataNames()
    {
        // GIVEN an unconnected context and audit metadata containing a recovery token.
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer("not-a-database-connection")
            .Options;
        using var database = new WorkbenchDbContext(options, new TenantContext(Guid.NewGuid()));
        var writer = new SecurityAuditWriter(database, TimeProvider.System);

        // WHEN appending the event THEN sensitive metadata is rejected before persistence.
        var error = Assert.Throws<ArgumentException>(() => writer.AppendTenant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test",
            "User",
            Guid.NewGuid(),
            "Succeeded",
            "correlation",
            new Dictionary<string, string> { ["recoveryToken"] = "must-not-appear" }));
        Assert.Equal("metadata", error.ParamName);
    }
}
