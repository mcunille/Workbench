// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Gemology;

internal static class GemReferenceCurationSql
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string name, Guid accountId, Guid sessionId)
    {
        var command = new SqlCommand("Gemology." + name, connection, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@AccountId", accountId);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        return command;
    }

    internal static byte[]? Version(string? value) => value is null ? null : Convert.FromBase64String(value);
    internal static string? Version(SqlDataReader reader, string name) => reader[name] is byte[] bytes ? Convert.ToBase64String(bytes) : null;
    internal static GemReferenceDraftResponse Draft(SqlDataReader reader) => new(
        (Guid)reader["Id"], (Guid)reader["EntryId"], JsonSerializer.Deserialize<GemReferenceContent>((string)reader["ContentJson"], Json)!,
        Version(reader, "ExpectedPublishedRowVersion"), Version(reader, "RowVersion")!,
        (Guid)reader["CreatedBy"], (Guid)reader["UpdatedBy"], (DateTimeOffset)reader["CreatedAtUtc"],
        (DateTimeOffset)reader["UpdatedAtUtc"], new Dictionary<string, string[]>());

    internal static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            DECLARE @Result int;
            EXEC @Result=sys.sp_getapplock @Resource=N'Gemology.Publication',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Result<0 THROW 50042,'Catalog is busy. Retry the request.',1;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
