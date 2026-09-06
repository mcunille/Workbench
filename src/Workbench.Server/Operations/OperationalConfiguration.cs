// Copyright (c) 2026 The White Stag Collection.

using Azure.Identity;
using Azure.Storage.Blobs;
using Workbench.Server.Identity;
using Workbench.Server.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Workbench.Server.Operations;

public static class OperationalConfiguration
{
    public static void Validate(IConfiguration configuration, bool development)
    {
        var provider = configuration["Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider) && development)
        {
            return;
        }
        if (provider == "FileSystem")
        {
            RequireInstallationId(configuration);
            var root = configuration["Storage:Root"];
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
                !development && !configuration.GetValue<bool>("Storage:DurableVolume"))
            {
                throw new InvalidOperationException("Filesystem storage requires an absolute durable root.");
            }
            if (configuration.GetValue("Deployment:Replicas", 1) > 1 &&
                (!configuration.GetValue<bool>("Storage:SharedVolume") || !configuration.GetValue<bool>("Storage:AtomicSharedVolume")))
            {
                throw new InvalidOperationException("Filesystem replicas require a shared volume with verified atomic publication.");
            }
            using var directory = new ConfinedDirectory(root);
        }
        else if (provider == "Azure")
        {
            if (!Guid.TryParse(configuration["Storage:InstallationId"], out var installation) || installation == Guid.Empty ||
                !Uri.TryCreate(configuration["Storage:ContainerUri"], UriKind.Absolute, out var uri) ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || uri.Scheme != "https")
            {
                throw new InvalidOperationException("Azure storage requires an installation identifier and HTTPS container URI.");
            }
        }
        else
        {
            throw new InvalidOperationException("A supported durable blob provider must be configured.");
        }
        if (configuration.GetValue("Deployment:Replicas", 1) < 1)
        {
            throw new InvalidOperationException("Replica count must be positive.");
        }
    }

    public static IBlobStore? CreateStore(IConfiguration configuration) => configuration["Storage:Provider"] switch
    {
        "FileSystem" => new FileSystemBlobStore(configuration["Storage:Root"] ?? "", ProviderAlias(configuration)),
        "Azure" => new AzureBlobStore(new BlobContainerClient(new Uri(configuration["Storage:ContainerUri"]!),
            new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), AzureOptions()), Guid.Parse(configuration["Storage:InstallationId"]!), ProviderAlias(configuration)),
        _ => null,
    };

    private static string ProviderAlias(IConfiguration configuration)
    {
        RequireInstallationId(configuration);
        var location = configuration["Storage:Provider"] == "FileSystem"
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:Root"]!))
            : new Uri(configuration["Storage:ContainerUri"]!).AbsoluteUri.TrimEnd('/');
        var binding = $"{configuration["Storage:Provider"]}\n{location}\n{configuration["Storage:InstallationId"]}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(binding)));
    }

    private static void RequireInstallationId(IConfiguration configuration)
    {
        if (!Guid.TryParse(configuration["Storage:InstallationId"], out var installation) || installation == Guid.Empty)
        {
            throw new InvalidOperationException("Storage requires a stable nonempty installation UUID.");
        }
    }

    private static BlobClientOptions AzureOptions()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 1;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        return options;
    }

    public static SmtpOptions ReadSmtp(IConfiguration configuration)
    {
        var options = configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        options.PublicOrigin = configuration["PublicOrigin"] ?? options.PublicOrigin;
        if (configuration["Smtp:PasswordFile"] is { Length: > 0 } path)
        {
            options.Password = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        return options;
    }
}

internal sealed class OperationalConfigurationValidator(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Security.ProductionSecurityConfigurationValidator.IsOpenApiDocumentGeneration())
        {
            return Task.CompletedTask;
        }
        OperationalConfiguration.Validate(configuration, environment.IsDevelopment());
        if (configuration["Identity:DeliveryProvider"] == "Smtp")
        {
            OperationalConfiguration.ReadSmtp(configuration).Validate();
        }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class BlobReadinessCheck(IConfiguration configuration, IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            OperationalConfiguration.Validate(configuration, environment.IsDevelopment());
            var store = OperationalConfiguration.CreateStore(configuration);
            if (store is null)
            {
                return environment.IsDevelopment() ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Blob storage is not configured.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await store.CheckReadyAsync(deadline.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Blob storage readiness failed.");
        }
    }
}

internal sealed class SmtpReadinessCheck(IIdentityMessageDelivery delivery) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (delivery is SmtpIdentityMessageDelivery smtp)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await smtp.CheckReadyAsync(deadline.Token);
            }
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("SMTP readiness failed.");
        }
    }
}
