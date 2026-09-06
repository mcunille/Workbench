// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Operations;

namespace Workbench.Server.Storage;

public sealed record BlobManifest(int Version, string SchemaVersion, Guid BackupId, Guid InstallationId, string Database,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<BlobManifestEntry> Entries);

// Operator-only offline tooling. No web endpoint or ordinary tenant permission
// exposes this authority. Stop every replica and worker before invoking it.
public static class StorageMaintenanceCommand
{
    private const string SchemaVersion = "20260905222755_AddDurableWork";
    public static async Task RunAsync(string action, string connectionString, string database,
        IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
    {
        string Required(string key) => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("A maintenance argument is missing.");
        if (Required("--offline-confirmation") != "OFFLINE " + database)
        {
            throw new ArgumentException("Storage maintenance requires explicit offline confirmation.");
        }
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Required("--config-file")))
            .AddEnvironmentVariables().Build();
        OperationalConfiguration.Validate(configuration, development: false);
        var source = OperationalConfiguration.CreateStore(configuration)!;
        await source.CheckReadyAsync(cancellationToken);
        if (!Guid.TryParse(configuration["Storage:InstallationId"], out var installation) || installation == Guid.Empty)
        {
            throw new ArgumentException("A storage installation identifier is required for maintenance.");
        }
        var entries = await ReadEntriesAsync(connectionString, cancellationToken);
        if (entries.Any(entry => entry.ProviderAlias != source.Alias))
        {
            throw new InvalidOperationException("The selected provider does not cover every retained revision.");
        }
        if (action is "manifest" or "snapshot")
        {
            foreach (var entry in entries)
            {
                await BlobMaintenance.VerifyAsync(source, entry, cancellationToken);
            }
            if (action == "snapshot")
            {
                var target = Target(configuration);
                foreach (var entry in entries)
                {
                    await BlobMaintenance.CopyAsync(source, target, entry, cancellationToken);
                }
            }
            await WriteNewAsync(Required("--output-file"),
                new BlobManifest(1, SchemaVersion, Guid.NewGuid(), installation, database, DateTimeOffset.UtcNow, entries), cancellationToken);
        }
        else if (action == "verify")
        {
            await using var input = File.OpenRead(Required("--manifest-file"));
            var manifest = await JsonSerializer.DeserializeAsync<BlobManifest>(input, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("A valid blob manifest is required.");
            if (manifest.Version != 1 || manifest.SchemaVersion != SchemaVersion || manifest.Database != database || manifest.InstallationId != installation ||
                manifest.Entries.Count != entries.Count || !entries.SequenceEqual(manifest.Entries))
            {
                throw new InvalidDataException("The manifest does not match the restored database.");
            }
            foreach (var entry in entries)
            {
                await BlobMaintenance.VerifyAsync(source, entry, cancellationToken);
            }
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var complete = new SqlCommand("[Storage].[CompleteRecoveryVerification]", connection)
            { CommandType = CommandType.StoredProcedure };
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (action == "migrate")
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var ready = new SqlCommand("[Storage].[AssertMigrationReady]", connection)
            { CommandType = CommandType.StoredProcedure })
            {
                await ready.ExecuteNonQueryAsync(cancellationToken);
            }
            var target = Target(configuration);
            if (target.Alias == source.Alias)
            {
                throw new InvalidOperationException("Provider migration requires distinct provider aliases.");
            }
            foreach (var entry in entries)
            {
                await BlobMaintenance.CopyAsync(source, target, entry, cancellationToken);
            }
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var entry in entries)
            {
                await using var relocate = new SqlCommand("[Storage].[RelocateRevision]", connection, transaction)
                { CommandType = CommandType.StoredProcedure };
                relocate.Parameters.AddWithValue("@TenantId", entry.TenantId);
                relocate.Parameters.AddWithValue("@Id", entry.RevisionId);
                relocate.Parameters.AddWithValue("@OldAlias", entry.ProviderAlias);
                relocate.Parameters.AddWithValue("@NewAlias", target.Alias);
                relocate.Parameters.AddWithValue("@Length", entry.Length);
                relocate.Parameters.AddWithValue("@Sha256", entry.Sha256);
                await relocate.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        else if (action == "reconcile")
        {
            var problems = new List<object>();
            var known = entries.Select(entry => new BlobObjectId(entry.TenantId, entry.RevisionId)).ToHashSet();
            foreach (var entry in entries)
            {
                try { await BlobMaintenance.VerifyAsync(source, entry, cancellationToken); }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    problems.Add(new { entry.TenantId, entry.RevisionId, Reason = "MissingOrCorrupt" });
                }
            }
            await foreach (var item in source.ListAsync(cancellationToken))
            {
                if (!known.Contains(item))
                {
                    problems.Add(new { item.TenantId, item.RevisionId, Reason = "Unreferenced" });
                }
            }
            await WriteNewAsync(Required("--output-file"), new { Version = 1, Problems = problems }, cancellationToken);
            if (problems.Count > 0)
            {
                throw new InvalidDataException("Storage reconciliation requires attention.");
            }
        }
        else
        {
            throw new ArgumentException("Unsupported storage maintenance command.");
        }
    }

    private static IBlobStore Target(IConfiguration configuration)
    {
        var targetConfiguration = configuration.GetSection("Target");
        OperationalConfiguration.Validate(targetConfiguration, development: false);
        return OperationalConfiguration.CreateStore(targetConfiguration)!;
    }

    public static async Task<List<BlobManifestEntry>> ReadEntriesAsync(string connectionString, CancellationToken cancellationToken)
    {
        var entries = new List<BlobManifestEntry>();
        Guid? after = null;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            await using var command = new SqlCommand("[Storage].[ExportManifest]", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@AfterId", (object?)after ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = new BlobManifestEntry(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4));
                entries.Add(entry);
                after = entry.RevisionId;
                count++;
            }
            if (count < 500) { return entries; }
        }
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        await using var output = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(output, value, cancellationToken: cancellationToken);
    }
}
