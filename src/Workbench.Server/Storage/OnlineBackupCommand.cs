// Copyright (c) 2026 The White Stag Collection.

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace Workbench.Server.Storage;

public static class OnlineBackupCommand
{
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        string Required(string key) => !string.IsNullOrWhiteSpace(configuration["Backup:" + key])
            ? configuration["Backup:" + key]! : throw new ArgumentException("Backup configuration is incomplete.");
        try
        {
            var installation = Guid.Parse(Required("InstallationId"));
            var sourceUri = ContainerUri(Required("SourceContainer"));
            var targetUri = ContainerUri(Required("DestinationContainer"));
            if (sourceUri.Host == targetUri.Host) throw new ArgumentException("An independent backup account is required.");
            var sqlId = Required("SqlResourceId");
            var sourceId = Required("SourceAccountId");
            var destinationId = Required("DestinationAccountId");
            foreach (var id in new[] { sqlId, sourceId, destinationId })
                if (!id.StartsWith("/subscriptions/", StringComparison.Ordinal) || id.Contains('?') || id.Contains('#') || id.Contains(".."))
                    throw new ArgumentException("Invalid Azure resource identity.");
            if (!sourceUri.Host.Equals(sourceId.Split('/')[^1] + ".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
                !targetUri.Host.Equals(destinationId.Split('/')[^1] + ".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Storage resource binding differs.");
            var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            async Task<JsonDocument> ReadAsync(string id, string api) => JsonDocument.Parse(await http.GetStringAsync(
                "https://management.azure.com" + id + "?api-version=" + api, cancellationToken));
            using var sql = await ReadAsync(sqlId + "/backupShortTermRetentionPolicies/default", "2023-08-01");
            var sqlDays = sql.RootElement.GetProperty("properties").GetProperty("retentionDays").GetInt32();
            foreach (var accountId in new[] { sourceId, destinationId })
            {
                using var account = await ReadAsync(accountId, "2023-05-01");
                if (account.RootElement.GetProperty("properties").GetProperty("publicNetworkAccess").GetString() != "Disabled" ||
                    account.RootElement.GetProperty("sku").GetProperty("name").GetString() is not ("Standard_GRS" or "Standard_GZRS"))
                    throw new InvalidOperationException("Storage network or geographic protection changed.");
            }
            using var sourceProperties = await ReadAsync(sourceId + "/blobServices/default", "2023-05-01");
            var properties = sourceProperties.RootElement.GetProperty("properties");
            if (!properties.GetProperty("isVersioningEnabled").GetBoolean() ||
                !properties.GetProperty("deleteRetentionPolicy").GetProperty("enabled").GetBoolean() ||
                properties.GetProperty("deleteRetentionPolicy").GetProperty("days").GetInt32() < sqlDays + 2)
                throw new InvalidOperationException("Source protection no longer covers SQL retention.");
            using var policy = await ReadAsync(destinationId + "/blobServices/default/containers/" + targetUri.AbsolutePath.Trim('/') +
                "/immutabilityPolicies/default", "2023-05-01");
            var protection = policy.RootElement.GetProperty("properties");
            if (protection.GetProperty("state").GetString() != "Locked" ||
                protection.GetProperty("immutabilityPeriodSinceCreationInDays").GetInt32() < sqlDays + 2)
                throw new InvalidOperationException("Backup immutability protection is insufficient.");
            var source = new BlobContainerClient(sourceUri, credential);
            var destination = new BlobContainerClient(targetUri, credential);
            foreach (var container in new[] { source, destination })
                if ((await container.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.PublicAccess != Azure.Storage.Blobs.Models.PublicAccessType.None)
                    throw new InvalidOperationException("Backup storage must be private.");
            var adapter = new AzureBackupSource(source, installation);
            var archive = new AzureBackupArchive(destination);
            await archive.VerifyRetainedAsync(installation, cancellationToken);
            var result = await OnlineBackup.CaptureAsync(adapter, archive,
                new BackupMetadata(installation, sourceUri.AbsoluteUri, sqlId, sqlId.Split('/')[^1], sqlDays,
                    Required("ImageDigest"), Required("SchemaVersion"), Required("RecoveryKeyVersions").Split(';', StringSplitOptions.RemoveEmptyEntries)),
                Guid.NewGuid(), sqlDays + 2, TimeProvider.System, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Event = "OnlineBackupStatus",
                result.BackupId,
                result.Outcome,
                result.CompletedAtUtc,
                ObjectCount = result.Objects.Count,
                GapCount = result.Gaps.Count,
                adapter.IgnoredObjects
            }));
            return result.Outcome == "IntegrityChecked" ? 0 : 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("{\"Event\":\"OnlineBackupStatus\",\"Outcome\":\"Failed\"}");
            return 1;
        }
    }

    private static Uri ContainerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Trim('/').Split('/').Length != 1 || uri.AbsolutePath == "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("An Azure container URI without credentials is required.");
        return uri;
    }
}
