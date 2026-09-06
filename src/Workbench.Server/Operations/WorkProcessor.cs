// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.DataProtection;
using Workbench.Server.Identity;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MailKit.Net.Smtp;

namespace Workbench.Server.Operations;

public sealed class WorkProcessor(string connectionString, TenantContextProof proof,
    IDataProtectionProvider protection, IIdentityMessageDelivery delivery, IReadOnlyDictionary<string, IBlobStore> stores)
{
    private readonly Guid _owner = Guid.NewGuid();

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        WorkLease? lease;
        await using (var claimConnection = new SqlConnection(connectionString))
        {
            await claimConnection.OpenAsync(cancellationToken);
            await using var claim = new SqlCommand("[Operations].[ClaimWork]", claimConnection) { CommandType = CommandType.StoredProcedure };
            claim.Parameters.AddWithValue("@Owner", _owner);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            lease = await reader.ReadAsync(cancellationToken)
                ? new WorkLease(reader.GetGuid(0), reader.GetGuid(1), (WorkKind)reader.GetInt32(2), reader.GetGuid(3), reader.GetInt64(4)) : null;
        }
        if (lease is null)
        {
            return false;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await ExecuteAsync(lease, deadline.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cleanup.Token);
            await proof.ApplyAsync(connection, lease.TenantId, cleanup.Token);
            await using var retry = Command("[Operations].[RetryWork]", connection, null, lease);
            retry.Parameters.AddWithValue("@Transient", error is DependencyUnavailableException or TimeoutException or OperationCanceledException or
                System.Net.Sockets.SocketException or SmtpProtocolException ||
                error is SmtpCommandException smtp && (int)smtp.StatusCode is >= 400 and < 500 ||
                error is SqlException sql && sql.Number is -2 or 1205);
            await retry.ExecuteScalarAsync(cleanup.Token);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return true;
    }

    private async Task ExecuteAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await proof.ApplyAsync(connection, lease.TenantId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var locked = Command("[Operations].[LockWork]", connection, transaction, lease))
        {
            if (Convert.ToInt32(await locked.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                return;
            }
        }
        if (lease.Kind == WorkKind.DeliverIdentityMessage)
        {
            await DeliverAsync(connection, transaction, lease, cancellationToken);
        }
        else if (lease.Kind == WorkKind.DeleteAttachment)
        {
            await DeleteAsync(connection, transaction, lease, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Unsupported work kind.");
        }
        await using var complete = Command("[Operations].[CompleteWork]", connection, transaction, lease);
        if (Convert.ToInt32(await complete.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidOperationException("The work lease expired.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeliverAsync(SqlConnection connection, SqlTransaction transaction, WorkLease lease, CancellationToken cancellationToken)
    {
        if (!delivery.IsAvailable)
        {
            throw new InvalidOperationException("Identity delivery is unavailable.");
        }
        IdentityMessage? message = null;
        await using (var read = new SqlCommand("""
            SELECT w.[ProtectedPayload], o.[TokenHash], o.[Purpose], o.[ExpiresAtUtc], u.[Email]
            FROM [Operations].[WorkItems] w
            JOIN [Identity].[IdentityOperations] o WITH (UPDLOCK, HOLDLOCK)
                ON o.[Id] = w.[IdentityOperationId] AND o.[TenantId] = w.[TenantId]
            JOIN [Identity].[Users] u WITH (UPDLOCK, HOLDLOCK) ON u.[Id] = o.[UserId] AND u.[TenantId] = o.[TenantId]
            WHERE w.[Id] = @id AND o.[Id] = @operation AND o.[ConsumedAtUtc] IS NULL
                AND o.[ExpiresAtUtc] > SYSUTCDATETIME() AND o.[SecurityVersion] = u.[SecurityVersion]
                AND ((o.[Purpose] = 1 AND u.[State] = 1) OR (o.[Purpose] = 2 AND u.[State] = 3));
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("@id", lease.Id);
            read.Parameters.AddWithValue("@operation", lease.ReferenceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
            {
                var bytes = protection.CreateProtector("Workbench.IdentityOutbox.v1", lease.TenantId.ToString("N"), lease.Id.ToString("N"))
                    .Unprotect((byte[])reader.GetValue(0));
                try
                {
                    message = JsonSerializer.Deserialize<IdentityMessage>(bytes);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                }
                if (message is null || !SessionToken.TryHash(message.Token, out var hash) ||
                    !hash.SequenceEqual((byte[])reader.GetValue(1)) || (int)message.Purpose != reader.GetInt32(2) ||
                    message.ExpiresAtUtc != reader.GetFieldValue<DateTimeOffset>(3) || message.Recipient != reader.GetString(4))
                {
                    throw new InvalidOperationException("Identity delivery integrity check failed.");
                }
            }
        }
        if (message is not null)
        {
            await delivery.DeliverAsync(message, cancellationToken);
        }
    }

    private async Task DeleteAsync(SqlConnection connection, SqlTransaction transaction, WorkLease lease, CancellationToken cancellationToken)
    {
        var revisions = new List<(Guid Id, string Alias)>();
        await using (var read = new SqlCommand("""
            SELECT a.[Held], a.[DeletedAtUtc], a.[DeleteAfterUtc]
            FROM [Storage].[Attachments] a WITH (UPDLOCK, HOLDLOCK) WHERE a.[Id] = @id;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("@id", lease.ReferenceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetBoolean(0) || reader.IsDBNull(1) ||
                reader.IsDBNull(2) || reader.GetFieldValue<DateTimeOffset>(2) > DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("Attachment retention prevents deletion.");
            }
        }
        await using (var read = new SqlCommand("SELECT [Id], [ProviderAlias] FROM [Storage].[Revisions] WHERE [AttachmentId] = @id AND [State] IN (0,1)", connection, transaction))
        {
            read.Parameters.AddWithValue("@id", lease.ReferenceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                revisions.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }
        foreach (var revision in revisions)
        {
            if (!stores.TryGetValue(revision.Alias, out var store))
            {
                throw new InvalidOperationException("The attachment provider is unavailable.");
            }
            await store.DeleteAsync(new BlobObjectId(lease.TenantId, revision.Id), cancellationToken);
        }
    }

    private SqlCommand Command(string name, SqlConnection connection, SqlTransaction? transaction, WorkLease lease)
    {
        var command = new SqlCommand(name, connection, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Id", lease.Id);
        command.Parameters.AddWithValue("@Owner", _owner);
        command.Parameters.AddWithValue("@Generation", lease.Generation);
        return command;
    }

    private sealed record WorkLease(Guid Id, Guid TenantId, WorkKind Kind, Guid ReferenceId, long Generation);
}
