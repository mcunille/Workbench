// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.X509Certificates;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Operations;

public static class WorkerHost
{
    public static async Task RunAsync(bool once)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(new SafeTelemetryLoggerProvider(Console.Out));
        var configuration = builder.Configuration;
        OperationalConfiguration.Validate(configuration, builder.Environment.IsDevelopment());
        var connection = configuration.GetConnectionString("Worker")
            ?? Environment.GetEnvironmentVariable("WORKBENCH_WORKER_CONNECTION")
            ?? throw new InvalidOperationException("A dedicated worker database credential is required.");
        var proof = TenantContextProof.Parse(ProductionSecurityConfigurationValidator.RequireTenantContextProofKey(configuration));
        builder.Services.AddDbContext<WorkbenchDbContext>(options => options.UseSqlServer(connection));
        var protection = builder.Services.AddDataProtection().SetApplicationName("Workbench")
            .PersistKeysToDbContext<WorkbenchDbContext>().DisableAutomaticKeyGeneration();
        if (!builder.Environment.IsDevelopment())
        {
            var path = ProductionSecurityConfigurationValidator.GetCertificatePath(configuration)
                ?? throw new InvalidOperationException("The worker requires the shared protection certificate.");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path,
                configuration["DataProtection:CertificatePassword"] ?? Environment.GetEnvironmentVariable("WORKBENCH_DATA_PROTECTION_CERTIFICATE_PASSWORD"),
                X509KeyStorageFlags.EphemeralKeySet);
            protection.ProtectKeysWithCertificate(certificate);
        }
        IIdentityMessageDelivery delivery = configuration["Identity:DeliveryProvider"] == "Smtp"
            ? new SmtpIdentityMessageDelivery(OperationalConfiguration.ReadSmtp(configuration))
            : new DisabledIdentityMessageDelivery();
        if (delivery is SmtpIdentityMessageDelivery)
        {
            OperationalConfiguration.ReadSmtp(configuration).Validate();
        }
        var store = OperationalConfiguration.CreateStore(configuration);
        var stores = new Dictionary<string, IBlobStore>(StringComparer.Ordinal);
        if (store is not null)
        {
            stores.Add(store.Alias, store);
        }
        builder.Services.AddSingleton(services => new WorkProcessor(connection, proof,
            services.GetRequiredService<IDataProtectionProvider>(), delivery, stores));
        using var host = builder.Build();
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var processor = host.Services.GetRequiredService<WorkProcessor>();
        try
        {
            do
            {
                try
                {
                    var processed = await processor.RunOnceAsync(lifetime.ApplicationStopping);
                    if (!processed && !once)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), lifetime.ApplicationStopping);
                    }
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    host.Services.GetRequiredService<ILogger<WorkProcessor>>().LogError(new EventId(2101), error, "Worker iteration failed.");
                    if (once)
                    {
                        Environment.ExitCode = 1;
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), lifetime.ApplicationStopping);
                    }
                }
            } while (!once && !lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }
}
