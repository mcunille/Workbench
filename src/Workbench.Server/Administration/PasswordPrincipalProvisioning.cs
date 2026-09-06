// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Administration;

public sealed record PasswordPrincipal(string User, string PasswordFile, string Role);

public static class PasswordPrincipalProvisioning
{
    private static readonly string[] Roles = ["workbench_web", "workbench_operator", "workbench_migrator"];

    public static async Task ProvisionAsync(string connectionString, IReadOnlyList<PasswordPrincipal> definitions, byte[] proofKey)
    {
        if (proofKey.Length != 32 || definitions.Count != Roles.Length ||
            definitions.Any(definition => definition is null || string.IsNullOrEmpty(definition.User) ||
                !System.Text.RegularExpressions.Regex.IsMatch(definition.User, @"\A[A-Za-z][A-Za-z0-9_]{2,63}\z") ||
                !Roles.Contains(definition.Role, StringComparer.Ordinal)) ||
            definitions.Select(definition => definition.User).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Roles.Length ||
            definitions.Select(definition => definition.Role).Distinct(StringComparer.Ordinal).Count() != Roles.Length)
        {
            throw new ArgumentException("Provide three distinct contained users for the Workbench roles and a 32-byte proof key.");
        }

        // Load every input before opening a transaction; never leave earlier roles behind on an input error.
        var prepared = new List<(string User, string Role, string Password)>();
        foreach (var definition in definitions)
        {
            var password = (await File.ReadAllTextAsync(definition.PasswordFile)).TrimEnd('\r', '\n');
            if (password.Length < 16)
            {
                throw new ArgumentException("Database principal passwords must contain at least 16 characters.");
            }
            prepared.Add((definition.User, definition.Role, password));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var guard = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'Workbench.PasswordProvisioning',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @result < 0 THROW 50030, 'Principal provisioning lock unavailable.', 1;
            """, connection, transaction))
        {
            await guard.ExecuteNonQueryAsync();
        }

        foreach (var definition in prepared)
        {
            // Identifier grammar is closed above. Existing users keep their password and must carry only this role.
            var escapedPassword = definition.Password.Replace("'", "''", StringComparison.Ordinal);
            await using var command = new SqlCommand($"""
                DECLARE @userId int = USER_ID(N'{definition.User}');
                DECLARE @roleId int = DATABASE_PRINCIPAL_ID(N'{definition.Role}');
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE principal_id=@roleId AND type='R' AND is_fixed_role=0)
                    THROW 50030, 'Expected Workbench role is missing.', 1;
                IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id=@roleId)
                    THROW 50030, 'Workbench role has incompatible inherited authority.', 1;
                IF @userId IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM sys.database_principals WHERE principal_id=@userId AND principal_id>4
                        AND type='S' AND authentication_type=2)
                    THROW 50030, 'Existing principal is not a contained password user.', 1;
                IF EXISTS (SELECT 1 FROM sys.database_role_members
                    WHERE member_principal_id=@userId AND role_principal_id<>@roleId)
                    THROW 50030, 'Existing principal has incompatible role membership.', 1;
                IF EXISTS (SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id=@userId
                    AND NOT (class=0 AND permission_name=N'CONNECT' AND state='G'))
                    THROW 50030, 'Existing principal has explicit permissions.', 1;
                IF EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.objects WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.database_principals WHERE owning_principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.types WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.assemblies WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.certificates WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE principal_id=@userId)
                    OR EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE principal_id=@userId)
                    THROW 50030, 'Existing principal owns database securables.', 1;
                IF @userId IS NULL
                    CREATE USER [{definition.User}] WITH PASSWORD=N'{escapedPassword}';
                IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
                    WHERE member_principal_id=USER_ID(N'{definition.User}') AND role_principal_id=@roleId)
                    ALTER ROLE [{definition.Role}] ADD MEMBER [{definition.User}];
                """, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await using var proof = new SqlCommand(
            "UPDATE [Security].[TenantContextKeys] SET [ProofKey]=@proof WHERE [Id]=1", connection, transaction);
        proof.Parameters.Add("@proof", SqlDbType.Binary, 32).Value = proofKey;
        if (await proof.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException("The tenant context proof key store is missing.");
        }
        await transaction.CommitAsync();
    }
}
