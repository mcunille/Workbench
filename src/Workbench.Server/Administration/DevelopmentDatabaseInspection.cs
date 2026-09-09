// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Administration;

public sealed record DevelopmentDatabaseReport(
    string[] KnownMigrations,
    string[] AppliedMigrations,
    bool MigrationHistoryCompatible,
    bool SchemaCurrent,
    bool SchemaInitialized,
    bool BootstrapCompleted,
    string[] PrincipalRolesPresent);

public static class DevelopmentDatabaseInspection
{
    public static bool IsCompatibleHistory(IReadOnlyList<string> known, IReadOnlyList<string> applied) =>
        applied.Count <= known.Count && applied.SequenceEqual(known.Take(applied.Count), StringComparer.Ordinal);

    public static async Task<DevelopmentDatabaseReport> InspectAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Tenant RLS intentionally hides rows from workload identities. Only the setup owner
        // can establish the same whole-database empty condition used by ProvisionTenant.
        await using (var owner = new SqlCommand("SELECT USER_NAME()", connection))
        {
            if (!string.Equals(await owner.ExecuteScalarAsync(cancellationToken) as string, "dbo", StringComparison.Ordinal))
                throw new InvalidOperationException("Development inspection requires the database owner.");
        }

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(connection).Options;
        await using var database = new WorkbenchDbContext(options);
        var known = database.Database.GetMigrations().ToArray();
        var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var compatible = IsCompatibleHistory(known, applied);
        await using var schema = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'[Tenancy].[Tenants]', N'U') IS NULL THEN 0 ELSE 1 END", connection);
        var initialized = Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken)) == 1;
        var bootstrapped = false;
        if (initialized)
        {
            await using var bootstrap = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM [Tenancy].[Tenants]) THEN 1 ELSE 0 END", connection);
            bootstrapped = Convert.ToInt32(await bootstrap.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        // This is presence information, not a security attestation. The existing idempotent
        // PasswordPrincipalProvisioning command remains authoritative for principal validation.
        await using var roles = new SqlCommand("""
            SELECT name FROM sys.database_principals
            WHERE type='R' AND is_fixed_role=0
                AND name IN (N'workbench_web', N'workbench_operator', N'workbench_migrator')
            ORDER BY name
            """, connection);
        var present = new List<string>();
        await using var reader = await roles.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) present.Add(reader.GetString(0));
        return new(known, applied, compatible, compatible && known.Length == applied.Length,
            initialized, bootstrapped, present.ToArray());
    }
}
