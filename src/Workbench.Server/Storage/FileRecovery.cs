// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using System.Text;

namespace Workbench.Server.Storage;

public sealed record RecoveryRevision(Guid TenantId, Guid RevisionId, string ProviderAlias, long? Length, string? Sha256,
    int State, string RowVersion);
public sealed record RecoveryInventory(string Database, string Server, long Generation, IReadOnlyList<RecoveryRevision> Rows);
public sealed record MissingRecoveryFile(Guid TenantId, Guid RevisionId, string Reason);
public sealed record FileRecoveryReport(int Version, Guid ReportId, Guid InstallationId, string TargetAlias, string InventoryJson,
    string Fingerprint, IReadOnlyList<MissingRecoveryFile> Missing, IReadOnlyList<BlobObjectId> Orphans);

public static class FileRecovery
{
    public static async Task<FileRecoveryReport> InspectAsync(RecoveryInventory inventory, string inventoryJson,
        IBlobStore target, Guid installationId, CancellationToken cancellationToken)
    {
        if (installationId == Guid.Empty || inventory.Generation <= 0 || inventory.Rows.Any(row => row.State == 0) ||
            inventory.Rows.Any(row => row.ProviderAlias == target.Alias))
            throw new InvalidOperationException("A distinct isolated target and resolved SQL operations are required.");
        await target.CheckReadyAsync(cancellationToken);
        var missing = new List<MissingRecoveryFile>();
        foreach (var row in inventory.Rows.Where(row => row.State == 1))
        {
            if (row.Length is null || row.Sha256 is null) throw new InvalidDataException("Invalid SQL content metadata.");
            try
            {
                await BlobMaintenance.VerifyAsync(target,
                    new BlobManifestEntry(row.TenantId, row.RevisionId, target.Alias, row.Length.Value, row.Sha256), cancellationToken);
            }
            catch (FileNotFoundException) { missing.Add(new(row.TenantId, row.RevisionId, "Missing")); }
            catch (InvalidDataException) { missing.Add(new(row.TenantId, row.RevisionId, "Corrupt")); }
        }
        // Every SQL row protects its identity from orphan deletion, including grace/purged history.
        var known = inventory.Rows.Select(row => new BlobObjectId(row.TenantId, row.RevisionId)).ToHashSet();
        var orphans = new HashSet<BlobObjectId>();
        await foreach (var id in target.ListAsync(cancellationToken))
            if (!known.Contains(id)) orphans.Add(id);
        await target.CheckReadyAsync(cancellationToken);
        return new FileRecoveryReport(1, Guid.NewGuid(), installationId, target.Alias, inventoryJson,
            Fingerprint(inventoryJson), missing, orphans.OrderBy(id => id.TenantId).ThenBy(id => id.RevisionId).ToArray());
    }

    // SQL HASHBYTES receives nvarchar: its exact UTF-16 bytes are the report concurrency token.
    public static string Fingerprint(string inventoryJson) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(inventoryJson)));
}
