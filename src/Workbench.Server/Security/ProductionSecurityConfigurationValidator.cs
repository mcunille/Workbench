// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Security;

public sealed class ProductionSecurityConfigurationValidator(
    IHostEnvironment environment,
    IConfiguration configuration) : IHostedService
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

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string? GetCertificatePath(IConfiguration configuration) =>
        configuration["DataProtection:CertificatePath"]
        ?? Environment.GetEnvironmentVariable("WORKBENCH_DATA_PROTECTION_CERTIFICATE_PATH");

    public static string? GetWebConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("Workbench")
        ?? Environment.GetEnvironmentVariable("WORKBENCH_WEB_CONNECTION");

    private static bool IsOpenApiDocumentGeneration() =>
        string.Equals(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
            "GetDocument.Insider",
            StringComparison.Ordinal);
}
