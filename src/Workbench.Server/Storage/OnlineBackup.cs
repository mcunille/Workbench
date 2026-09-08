// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workbench.Server.Storage;

public sealed record BackupVersion(string Name, string VersionId, string ETag, long Length, Guid TenantId, Guid RevisionId);
public sealed record BackupObject(BackupVersion Source, string Destination, long Length, string Sha256);
public sealed record BackupMetadata(Guid InstallationId, string SourceContainer, string SqlResourceId, string Database,
    int SqlRetentionDays, string ImageDigest, string SchemaVersion, IReadOnlyList<string> RecoveryKeyVersions);
public sealed record BackupCatalog(int Version, Guid BackupId, BackupMetadata Metadata, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, DateTimeOffset ExpiresAtUtc, string Outcome,
    IReadOnlyList<BackupObject> Objects, IReadOnlyList<string> Gaps);

public interface IBackupVersionSource
{
    IAsyncEnumerable<BackupVersion> ListAsync(CancellationToken cancellationToken);
    Task<Stream> OpenAsync(BackupVersion version, CancellationToken cancellationToken);
}

public interface IBackupArchive
{
    Task CreateAsync(string name, Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenAsync(string name, CancellationToken cancellationToken);
}

public static class OnlineBackup
{
    public static async Task<BackupCatalog> CaptureAsync(IBackupVersionSource source, IBackupArchive archive,
        BackupMetadata metadata, Guid backupId, int retentionDays, TimeProvider time, CancellationToken cancellationToken)
    {
        if (backupId == Guid.Empty || metadata.InstallationId == Guid.Empty || metadata.SqlRetentionDays is < 1 or > 35 ||
            retentionDays < metadata.SqlRetentionDays + 2 || retentionDays > 37 ||
            string.IsNullOrWhiteSpace(metadata.SchemaVersion) || metadata.RecoveryKeyVersions.Count == 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(metadata.ImageDigest, "^sha256:[a-f0-9]{64}$"))
            throw new ArgumentException("Backup identity, release or retention is invalid.");

        var started = time.GetUtcNow();
        var objects = new List<BackupObject>();
        var gaps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prefix = $"{metadata.InstallationId:N}/{backupId:N}/";
        await foreach (var version in source.ListAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(version.VersionId) || string.IsNullOrWhiteSpace(version.ETag) ||
                version.Length is < 0 or > 25 * 1024 * 1024 || version.TenantId == Guid.Empty || version.RevisionId == Guid.Empty)
                throw new InvalidDataException("Invalid version inventory.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { version.Name, version.VersionId }))));
            if (!seen.Add(key)) continue;
            using var content = new MemoryStream();
            BlobContentIdentity identity;
            try
            {
                await using var input = await source.OpenAsync(version, cancellationToken);
                identity = await BlobTransfer.CopyAsync(input, content, Math.Max(1, version.Length), cancellationToken);
            }
            catch (FileNotFoundException)
            {
                gaps.Add(key);
                continue;
            }
            if (identity.Length != version.Length) throw new InvalidDataException("Source version length changed.");
            var destination = prefix + "objects/" + key;
            content.Position = 0;
            try { await archive.CreateAsync(destination, content, cancellationToken); }
            catch (BackupObjectExistsException) { /* Retry still requires exact content verification. */ }
            await using (var stored = await archive.OpenAsync(destination, cancellationToken))
            {
                var actual = await BlobTransfer.CopyAsync(stored, Stream.Null, Math.Max(1, version.Length), cancellationToken);
                if (actual != identity) throw new InvalidDataException("Backup destination integrity failed.");
            }
            objects.Add(new BackupObject(version, destination, identity.Length, identity.Sha256));
        }
        var finished = time.GetUtcNow();
        if (finished - started > TimeSpan.FromDays(1)) throw new InvalidDataException("Backup exceeded its capture window.");
        var catalog = new BackupCatalog(1, backupId, metadata, started, finished, started.AddDays(retentionDays),
            gaps.Count == 0 ? "IntegrityChecked" : "Incomplete", objects, gaps);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        using var catalogStream = new MemoryStream(bytes, writable: false);
        await archive.CreateAsync(prefix + "catalog.json", catalogStream, cancellationToken);
        await using var catalogRead = await archive.OpenAsync(prefix + "catalog.json", cancellationToken);
        var verified = await BlobTransfer.CopyAsync(catalogRead, Stream.Null, bytes.Length, cancellationToken);
        if (verified.Length != bytes.Length || verified.Sha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("Backup catalog integrity failed.");
        return catalog;
    }
}

public sealed class BackupObjectExistsException : IOException;
