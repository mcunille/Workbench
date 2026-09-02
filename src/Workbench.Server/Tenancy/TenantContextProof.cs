// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Tenancy;

public sealed class TenantContextProof
{
    public const int KeySize = 32;
    private readonly byte[] _key;

    public TenantContextProof(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
        {
            throw new ArgumentException("The tenant context proof key must contain exactly 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    public static TenantContextProof Parse(string encodedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedKey);
        try
        {
            return new TenantContextProof(Convert.FromBase64String(encodedKey));
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException("The tenant context proof key is not valid Base64.", error);
        }
    }

    public async Task ApplyAsync(
        DbConnection connection,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var nonce = tenantId.HasValue ? RandomNumberGenerator.GetBytes(KeySize) : null;
        var proof = tenantId.HasValue ? Compute(tenantId.Value, nonce!) : null;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=1;
            EXEC sys.sp_set_session_context @key=N'TenantNonce', @value=@nonce, @read_only=1;
            EXEC sys.sp_set_session_context @key=N'TenantProof', @value=@proof, @read_only=1;
            """;
        command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier)
        {
            Value = tenantId.HasValue ? tenantId.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@nonce", SqlDbType.VarBinary, KeySize)
        {
            Value = nonce is null ? DBNull.Value : nonce,
        });
        command.Parameters.Add(new SqlParameter("@proof", SqlDbType.VarBinary, KeySize)
        {
            Value = proof is null ? DBNull.Value : proof,
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private byte[] Compute(Guid tenantId, byte[] nonce)
    {
        var tenantBytes = Encoding.Unicode.GetBytes(tenantId.ToString("D").ToUpperInvariant());
        var input = new byte[_key.Length + tenantBytes.Length + nonce.Length];
        Buffer.BlockCopy(_key, 0, input, 0, _key.Length);
        Buffer.BlockCopy(tenantBytes, 0, input, _key.Length, tenantBytes.Length);
        Buffer.BlockCopy(nonce, 0, input, _key.Length + tenantBytes.Length, nonce.Length);
        return SHA256.HashData(input);
    }
}
