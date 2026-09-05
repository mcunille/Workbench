// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Security;

public sealed class ProductionSecurityConfigurationValidator(
    IHostEnvironment environment,
    IConfiguration configuration,
    IEnumerable<IIdentityMessageDelivery> deliveries,
    IEnumerable<ISensitiveRequestRateLimiter> rateLimiters) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsProduction() || IsOpenApiDocumentGeneration())
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(GetCertificatePath(configuration)))
        {
            throw new InvalidOperationException(
                "Production requires a data-protection certificate path.");
        }

        if (string.IsNullOrWhiteSpace(GetWebConnectionString(configuration)))
        {
            throw new InvalidOperationException(
                "Production requires the Workbench web database connection.");
        }

        _ = TenantContextProof.Parse(RequireTenantContextProofKey(configuration));

        if (!System.Net.IPAddress.TryParse(GetKnownProxy(configuration), out _))
        {
            throw new InvalidOperationException(
                "Production requires a valid trusted proxy IP address.");
        }

        var publicIdentityEnabled = configuration.GetValue<bool>("Identity:PublicRecoveryEnabled") ||
            configuration.GetValue<bool>("Identity:PublicInvitationEnabled");
        if (publicIdentityEnabled &&
            (!deliveries.Any(provider => provider.IsAvailable &&
                provider is not DevelopmentIdentityMessageDelivery) ||
             !rateLimiters.Any(provider => provider.IsAvailable &&
                provider is not DevelopmentSensitiveRequestRateLimiter)))
        {
            throw new InvalidOperationException(
                "Public recovery and invitations require production message delivery and shared rate limiting.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string? GetCertificatePath(IConfiguration configuration) =>
        configuration["DataProtection:CertificatePath"]
        ?? Environment.GetEnvironmentVariable("WORKBENCH_DATA_PROTECTION_CERTIFICATE_PATH");

    public static string? GetWebConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("Workbench")
        ?? Environment.GetEnvironmentVariable("WORKBENCH_WEB_CONNECTION");

    public static string? GetKnownProxy(IConfiguration configuration) =>
        configuration["ReverseProxy:KnownProxy"]
        ?? Environment.GetEnvironmentVariable("WORKBENCH_KNOWN_PROXY");

    public static string RequireTenantContextProofKey(IConfiguration configuration)
    {
        var directValue = configuration["TenantContext:ProofKey"]
            ?? Environment.GetEnvironmentVariable("WORKBENCH_TENANT_CONTEXT_PROOF_KEY");
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        var path = configuration["TenantContext:ProofKeyFile"]
            ?? Environment.GetEnvironmentVariable("WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("The tenant context proof key is not configured.");
        }

        return File.ReadAllText(path).Trim();
    }

    private static bool IsOpenApiDocumentGeneration() =>
        string.Equals(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
            "GetDocument.Insider",
            StringComparison.Ordinal);
}
