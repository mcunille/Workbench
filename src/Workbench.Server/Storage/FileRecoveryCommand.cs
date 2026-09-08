// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace Workbench.Server.Storage;

public static class FileRecoveryCommand
{
    public static async Task RunAsync(string action, string connectionString, IReadOnlyDictionary<string, string> arguments,
        IConfiguration configuration, IBlobStore target, Guid installation, CancellationToken cancellationToken)
    {
        string Required(string key) => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("A recovery argument is missing.");
        var path = Path.GetFullPath(Required("--report-file"));
        FileRecoveryReport? accepted = null;
        if (action == "recovery-apply")
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != Required("--accept-report-sha256"))
                throw new InvalidDataException("The accepted report digest differs.");
            accepted = JsonSerializer.Deserialize<FileRecoveryReport>(bytes) ?? throw new InvalidDataException("Invalid recovery report.");
            if (accepted.Version != 1 || accepted.ReportId == Guid.Empty || accepted.InstallationId != installation || accepted.TargetAlias != target.Alias)
                throw new InvalidDataException("Recovery target identity changed.");
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (accepted is not null)
        {
            var prior = JsonSerializer.Deserialize<RecoveryInventory>(accepted.InventoryJson) ?? throw new InvalidDataException("Invalid recovery inventory.");
            if (prior.Database != connection.Database || accepted.Fingerprint != FileRecovery.Fingerprint(accepted.InventoryJson))
                throw new InvalidDataException("Recovery database or fingerprint changed.");
            await using var retry = new SqlCommand("[Storage].[ReadFileRecoveryCompletion]", connection, transaction) { CommandType = CommandType.StoredProcedure };
            retry.Parameters.AddWithValue("@ReportId", accepted.ReportId);
            retry.Parameters.AddWithValue("@Generation", prior.Generation);
            retry.Parameters.AddWithValue("@Fingerprint", accepted.Fingerprint);
            retry.Parameters.AddWithValue("@TargetAlias", target.Alias);
            if ((int)(await retry.ExecuteScalarAsync(cancellationToken))! == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                Console.WriteLine("File recovery was already applied; no changes made.");
                return;
            }
        }
        var json = await ReadInventoryAsync(connection, transaction, cancellationToken);
        var inventory = JsonSerializer.Deserialize<RecoveryInventory>(json) ?? throw new InvalidDataException("Missing recovery inventory.");
        var originalAlias = RecoveryBinding.Validate(configuration, inventory);
        if (action == "recovery-plan")
        {
            // Validate the isolated binding before any materialization can write provider bytes.
            if (inventory.Rows.Any(row => row.ProviderAlias == target.Alias) || inventory.Rows.Any(row => row.State == 0))
                throw new InvalidOperationException("Use a distinct recovery store and resolve pending SQL operations first.");
            if (arguments.TryGetValue("--catalog-directory", out var directory))
                await MaterializeAsync(directory, configuration, inventory, target, installation, originalAlias, cancellationToken);
            var report = await FileRecovery.InspectAsync(inventory, json, target, installation, cancellationToken);
            await WriteNewAsync(path, report, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Event = "FileRecoveryPlanned",
                report.ReportId,
                MissingCount = report.Missing.Count,
                OrphanCount = report.Orphans.Count,
                ReportSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)))
            }));
            return;
        }
        if (action != "recovery-apply" || accepted is null) throw new ArgumentException("Unknown recovery action.");
        if (accepted.Version != 1 || accepted.ReportId == Guid.Empty || accepted.InstallationId != installation || accepted.TargetAlias != target.Alias ||
            accepted.InventoryJson != json || accepted.Fingerprint != FileRecovery.Fingerprint(json))
            throw new InvalidDataException("Recovery identity or SQL inventory changed. Prepare a new report.");
        var current = await FileRecovery.InspectAsync(inventory, json, target, installation, cancellationToken);
        if (!current.Missing.SequenceEqual(accepted.Missing) || !current.Orphans.SequenceEqual(accepted.Orphans))
            throw new InvalidDataException("Recovered content changed. Prepare a new report.");
        foreach (var orphan in current.Orphans) await target.DeleteAsync(orphan, cancellationToken);
        var after = await FileRecovery.InspectAsync(inventory, json, target, installation, cancellationToken);
        if (after.Orphans.Count != 0 || !after.Missing.SequenceEqual(accepted.Missing))
            throw new InvalidDataException("Recovery cleanup verification failed.");
        await using var complete = new SqlCommand("[Storage].[AcceptFileRecovery]", connection, transaction) { CommandType = CommandType.StoredProcedure };
        complete.Parameters.AddWithValue("@ReportId", accepted.ReportId);
        complete.Parameters.AddWithValue("@Generation", inventory.Generation);
        complete.Parameters.AddWithValue("@Fingerprint", accepted.Fingerprint);
        complete.Parameters.AddWithValue("@TargetAlias", target.Alias);
        complete.Parameters.AddWithValue("@Missing", JsonSerializer.Serialize(accepted.Missing));
        await complete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Event = "FileRecoveryCompleted",
            accepted.ReportId,
            Outcome = accepted.Missing.Count == 0 ? "RestoreVerified" : "RestoreVerifiedWithMissingFiles",
            MissingCount = accepted.Missing.Count
        }));
    }

    public static async Task<string> ReadInventoryAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("[Storage].[ReadRecoveryInventory]", connection, transaction) { CommandType = CommandType.StoredProcedure };
        var output = command.Parameters.Add("@Inventory", SqlDbType.NVarChar, -1);
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return output.Value as string ?? throw new InvalidDataException("No recovery inventory was returned.");
    }

    private static async Task MaterializeAsync(string directory, IConfiguration configuration, RecoveryInventory inventory,
        IBlobStore target, Guid installation, string originalAlias, CancellationToken cancellationToken)
    {
        var uri = new Uri(configuration["Recovery:ArchiveContainer"] ?? throw new ArgumentException("Backup archive binding is required."));
        if (uri.Scheme != "https" || !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Invalid archive URI.");
        if (configuration["Storage:ContainerUri"]?.TrimEnd('/') == uri.AbsoluteUri.TrimEnd('/'))
            throw new ArgumentException("The recovery target cannot be the archive.");
        var sqlId = configuration["Recovery:OriginalSqlResourceId"] ?? throw new ArgumentException("Original SQL binding is required.");
        var archive = new AzureBackupArchive(new BlobContainerClient(uri, new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)));
        var candidates = new List<(BackupObject Object, string SourceAlias)>();
        foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(directory), "*.json"))
        {
            await using var input = File.OpenRead(file);
            var catalog = await JsonSerializer.DeserializeAsync<BackupCatalog>(input, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Invalid catalog.");
            if (catalog.Version != 1 || catalog.Metadata.InstallationId != installation || catalog.Metadata.SqlResourceId != sqlId ||
                catalog.ExpiresAtUtc <= DateTimeOffset.UtcNow || catalog.Outcome is not ("IntegrityChecked" or "Incomplete"))
                throw new InvalidDataException("Catalog identity or retention differs.");
            var prefix = $"{installation:N}/{catalog.BackupId:N}/objects/";
            if (new Uri(catalog.Metadata.SourceContainer) != new Uri(configuration["Recovery:Source:Storage:ContainerUri"]!))
                throw new InvalidDataException("Catalog source does not match verified original storage.");
            var sourceAlias = originalAlias;
            foreach (var entry in catalog.Objects)
            {
                if (!entry.Destination.StartsWith(prefix, StringComparison.Ordinal) || entry.Destination.Length != prefix.Length + 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(entry.Destination[prefix.Length..], "^[A-F0-9]{64}$"))
                    throw new InvalidDataException("Invalid archive object binding.");
                candidates.Add((entry, sourceAlias));
            }
        }
        foreach (var row in inventory.Rows.Where(row => row.State == 1))
        {
            foreach (var candidate in candidates.Where(entry => entry.SourceAlias == row.ProviderAlias &&
                entry.Object.Source.TenantId == row.TenantId && entry.Object.Source.RevisionId == row.RevisionId &&
                entry.Object.Length == row.Length && entry.Object.Sha256 == row.Sha256).Select(entry => entry.Object))
            {
                using var bytes = new MemoryStream();
                try
                {
                    await using var content = await archive.OpenAsync(candidate.Destination, cancellationToken);
                    var identity = await BlobTransfer.CopyAsync(content, bytes, Math.Max(1, candidate.Length), cancellationToken);
                    if (identity.Length != candidate.Length || identity.Sha256 != candidate.Sha256) continue;
                }
                catch (Azure.RequestFailedException error) when (error.Status == 404 && error.ErrorCode == "BlobNotFound") { continue; }
                catch (InvalidDataException) { continue; }
                bytes.Position = 0;
                var id = new BlobObjectId(row.TenantId, row.RevisionId);
                try { await target.StageAsync(id, bytes, Math.Max(1, candidate.Length), cancellationToken); }
                catch (IOException)
                {
                    // Existing retry data is never overwritten; it must verify after publication.
                }
                await target.PublishAsync(id, cancellationToken);
                await BlobMaintenance.VerifyAsync(target, new(row.TenantId, row.RevisionId, target.Alias, candidate.Length, candidate.Sha256), cancellationToken);
                break;
            }
        }
    }

    private static async Task WriteNewAsync(string path, FileRecoveryReport report, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(stream, report, cancellationToken: cancellationToken);
    }
}
