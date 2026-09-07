// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Administration;

public sealed record EntraPrincipal(string Name, Guid PrincipalId, Guid ClientId, string Role)
{
    public byte[] GetSqlSid() => ClientId.ToByteArray();
}

public sealed record EntraIdentityManifest(int Version, EntraPrincipal[] Identities);

public sealed class InvalidEntraManifestException : ArgumentException
{
    public InvalidEntraManifestException() : base(
        "Provide a version 1 identity manifest with identities containing name, role, principalId and clientId for all five roles. Legacy objectId manifests are unsupported; obtain both IDs from Azure. No SQL was changed.")
    {
    }
}

public static class EntraPrincipalProvisioning
{
    private static readonly string[] Roles = ["workbench_web", "workbench_worker", "workbench_migrator", "workbench_operator", "workbench_storage_maintenance"];

    public static EntraPrincipal[] ParseManifest(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<EntraIdentityManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            });
            if (manifest is null || manifest.Version != 1 || manifest.Identities is null)
            {
                throw new InvalidEntraManifestException();
            }
            Validate(manifest.Identities);
            return manifest.Identities;
        }
        catch (JsonException)
        {
            throw new InvalidEntraManifestException();
        }
        catch (ArgumentException)
        {
            throw new InvalidEntraManifestException();
        }
    }

    public static void Validate(IReadOnlyList<EntraPrincipal> principals)
    {
        if (principals.Count != Roles.Length || principals.Any(principal => principal is null ||
                principal.PrincipalId == Guid.Empty || principal.ClientId == Guid.Empty || principal.PrincipalId == principal.ClientId || string.IsNullOrEmpty(principal.Name) ||
                !System.Text.RegularExpressions.Regex.IsMatch(principal.Name, "^[A-Za-z][A-Za-z0-9_]{2,63}$") ||
                !Roles.Contains(principal.Role, StringComparer.Ordinal)) ||
            principals.Select(principal => principal.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Roles.Length ||
            principals.SelectMany(principal => new[] { principal.PrincipalId, principal.ClientId }).Distinct().Count() != Roles.Length * 2 ||
            principals.Select(principal => principal.Role).Distinct().Count() != Roles.Length)
        {
            throw new ArgumentException("Provide distinct named Entra identities for exactly the five Workbench roles.");
        }
    }

    public static async Task ProvisionAsync(string connectionString, IReadOnlyList<EntraPrincipal> principals,
        byte[] proofKey, CancellationToken cancellationToken)
    {
        Validate(principals);
        if (proofKey.Length != 32)
        {
            throw new ArgumentException("A 32-byte tenant context proof key is required.");
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'Workbench.EntraProvisioning',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @result < 0 THROW 50030, 'Principal provisioning lock unavailable.', 1;
            IF CAST(SERVERPROPERTY('EngineEdition') AS int) <> 5
                THROW 50030, 'Entra provisioning requires Azure SQL Database.', 1;
            IF EXISTS (SELECT 1 FROM [Tenancy].[Tenants]) AND
               NOT EXISTS (SELECT 1 FROM [Security].[TenantContextKeys] WHERE [Id]=1 AND [ProofKey]=@proof)
                THROW 50030, 'Provisioning cannot rotate an initialized installation proof.', 1;
            """, connection, transaction))
        {
            guard.Parameters.Add("@proof", SqlDbType.Binary, 32).Value = proofKey;
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var principal in principals)
        {
            // Azure SQL application users use the client ID; principal IDs belong to Azure RBAC/Exchange.
            var sid = principal.GetSqlSid();
            await using var command = new SqlCommand($"""
                IF USER_ID(N'{principal.Name}') IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM sys.database_principals WHERE name=N'{principal.Name}' AND sid=@sid AND type='E')
                    THROW 50030, 'Existing principal identity does not match.', 1;
                IF EXISTS (SELECT 1 FROM sys.database_principals WHERE sid=@sid AND name<>N'{principal.Name}')
                    THROW 50030, 'Identity already belongs to another principal.', 1;
                IF EXISTS (SELECT 1 FROM sys.database_role_members m JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
                    WHERE m.member_principal_id=USER_ID(N'{principal.Name}') AND r.name<>N'{principal.Role}')
                    THROW 50030, 'Existing principal has incompatible role membership.', 1;
                IF USER_ID(N'{principal.Name}') IS NULL
                    CREATE USER [{principal.Name}] WITH SID=0x{Convert.ToHexString(sid)}, TYPE=E;
                ALTER ROLE [{principal.Role}] ADD MEMBER [{principal.Name}];
                """, connection, transaction);
            command.Parameters.Add("@sid", SqlDbType.VarBinary, 16).Value = sid;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var proof = new SqlCommand("UPDATE [Security].[TenantContextKeys] SET [ProofKey]=@proof WHERE [Id]=1", connection, transaction);
        proof.Parameters.Add("@proof", SqlDbType.Binary, 32).Value = proofKey;
        if (await proof.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The tenant proof store is missing.");
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
