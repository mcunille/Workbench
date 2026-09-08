// Copyright (c) 2026 The White Stag Collection.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace Workbench.Server.Storage;

public static class BackupRetentionCommand
{
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var installation = Guid.Parse(configuration["Backup:InstallationId"] ?? "");
            var uri = new Uri(configuration["Backup:DestinationContainer"] ?? "");
            var id = configuration["Backup:DestinationAccountId"] ?? "";
            if (!Regex.IsMatch(id, @"^/subscriptions/[a-fA-F0-9-]{36}/resourceGroups/[^/?#]+/providers/Microsoft\.Storage/storageAccounts/[a-z0-9]{3,24}$") ||
                uri.Scheme != "https" || uri.Port != 443 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath.TrimEnd('/') != "/backups" || uri.Host != id.Split('/')[^1] + ".blob.core.windows.net")
                throw new ArgumentException("Invalid archive binding.");
            var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            async Task<JsonDocument> ReadAsync(string suffix) => JsonDocument.Parse(await http.GetStringAsync(
                "https://management.azure.com" + id + suffix + "?api-version=2023-05-01", cancellationToken));
            using var account = await ReadAsync("");
            using var service = await ReadAsync("/blobServices/default");
            using var policy = await ReadAsync("/blobServices/default/containers/backups/immutabilityPolicies/default");
            ValidateProtection(account.RootElement, service.RootElement, policy.RootElement);
            var container = new BlobContainerClient(uri, credential);
            if ((await container.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.PublicAccess != Azure.Storage.Blobs.Models.PublicAccessType.None)
                throw new InvalidOperationException("The archive must remain private.");
            var removed = await BackupRetention.ExpireAsync(new AzureBackupRetentionArchive(container), installation, DateTimeOffset.UtcNow, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new { Event = "BackupRetentionStatus", Outcome = "Succeeded", ExpiredObjectCount = removed }));
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("{\"Event\":\"BackupRetentionStatus\",\"Outcome\":\"Failed\"}");
            return 1;
        }
    }

    public static void ValidateProtection(JsonElement account, JsonElement service, JsonElement policy)
    {
        var properties = service.GetProperty("properties");
        var protection = policy.GetProperty("properties");
        if (account.GetProperty("properties").GetProperty("publicNetworkAccess").GetString() != "Disabled" ||
            account.GetProperty("sku").GetProperty("name").GetString() is not ("Standard_GRS" or "Standard_GZRS") ||
            protection.GetProperty("state").GetString() != "Locked" ||
            protection.GetProperty("immutabilityPeriodSinceCreationInDays").GetInt32() < 37 ||
            properties.TryGetProperty("isVersioningEnabled", out var versioning) && versioning.GetBoolean() ||
            properties.TryGetProperty("deleteRetentionPolicy", out var softDelete) && softDelete.GetProperty("enabled").GetBoolean())
            throw new InvalidOperationException("Archive protection or deletion semantics changed.");
    }
}
