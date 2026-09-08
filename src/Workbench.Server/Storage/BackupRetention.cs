// Copyright (c) 2026 The White Stag Collection.

using System.Text.RegularExpressions;

namespace Workbench.Server.Storage;

public sealed record RetentionObject(string Name, string ETag, DateTimeOffset CreatedAtUtc, DateTimeOffset ModifiedAtUtc);
public interface IBackupRetentionArchive
{
    Task<IReadOnlyList<RetentionObject>> ListAsync(Guid installation, CancellationToken cancellationToken);
    Task<BackupCatalog> ReadCatalogAsync(RetentionObject item, CancellationToken cancellationToken);
    Task DeleteAsync(RetentionObject item, CancellationToken cancellationToken);
}
public static class BackupRetention
{
    public static async Task<int> ExpireAsync(IBackupRetentionArchive archive, Guid installation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (installation == Guid.Empty) throw new ArgumentException("Installation is required.");
        var items = await archive.ListAsync(installation, cancellationToken);
        var byName = items.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var prefix = $"{installation:N}/";
        var pattern = "^" + prefix + "[a-f0-9]{32}/(catalog\\.json|objects/[A-F0-9]{64})$";
        foreach (var item in items)
            if (!Regex.IsMatch(item.Name, pattern) || string.IsNullOrWhiteSpace(item.ETag) ||
                item.CreatedAtUtc == default || item.ModifiedAtUtc == default ||
                !Guid.TryParseExact(item.Name.Substring(prefix.Length, 32), "N", out var runId) || runId == Guid.Empty)
                throw new InvalidDataException("Unexpected backup object identity or age.");

        // Validate every catalog before deleting anything. Valid catalogs only own their run's objects.
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Where(item => item.Name.EndsWith("/catalog.json", StringComparison.Ordinal)))
        {
            var catalog = await archive.ReadCatalogAsync(item, cancellationToken);
            var runPrefix = $"{installation:N}/{catalog.BackupId:N}/";
            if (catalog.Version != 1 || catalog.BackupId == Guid.Empty || catalog.Metadata.InstallationId != installation ||
                item.Name != runPrefix + "catalog.json" || catalog.ExpiresAtUtc == default ||
                catalog.Outcome is not ("IntegrityChecked" or "Incomplete"))
                throw new InvalidDataException("Invalid backup catalog identity.");
            foreach (var entry in catalog.Objects)
            {
                if (!entry.Destination.StartsWith(runPrefix + "objects/", StringComparison.Ordinal) || !Regex.IsMatch(entry.Destination, pattern))
                    throw new InvalidDataException("A catalog references objects outside its own capture.");
                if (catalog.ExpiresAtUtc > now && !byName.ContainsKey(entry.Destination))
                    throw new InvalidDataException("A retained catalog references a missing object.");
            }
            if (catalog.ExpiresAtUtc > now) retained.Add(runPrefix);
        }
        var removed = 0;
        foreach (var run in items.GroupBy(item => item.Name[..(prefix.Length + 33)]))
        {
            // Include catalog creation/modification age, not just the original source upload age.
            // New or still-running captures, including partially written runs, cannot expire.
            if (retained.Contains(run.Key) || run.Any(item => item.CreatedAtUtc > now.AddDays(-45) || item.ModifiedAtUtc > now.AddDays(-45))) continue;
            foreach (var item in run.OrderBy(item => item.Name.EndsWith("/catalog.json", StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await archive.DeleteAsync(item, cancellationToken);
                removed++;
            }
        }
        return removed;
    }
}
