// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Workbench.Server.Security;

public static class DeploymentSecrets
{
    public static string? ReadValue(IConfiguration configuration, string key, string? fallback = null)
    {
        if (configuration[key + "File"] is not { } path)
        {
            return configuration[key] ?? fallback;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A configured secret file path must not be empty.");
        }
        var value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("A configured secret file must not be empty.");
        }
        return value;
    }

    public static void ConfigureProtection(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        var current = LoadCertificate(configuration);
        List<X509Certificate2> certificates = [current];
        foreach (var previous in configuration.GetSection("DataProtection:PreviousCertificates").GetChildren())
        {
            var settings = new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = previous["Path"],
                ["DataProtection:CertificateFormat"] = previous["Format"],
                ["DataProtection:CertificatePassword"] = previous["Password"] ?? "",
                ["DataProtection:CertificatePasswordFile"] = previous["PasswordFile"],
            };
            certificates.Add(LoadCertificate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
        }
        builder.ProtectKeysWithCertificate(current);
        builder.UnprotectKeysWithAnyCertificate(certificates.ToArray());
    }

    public static X509Certificate2 LoadCertificate(IConfiguration configuration)
    {
        var format = configuration["DataProtection:CertificateFormat"] ?? "Pfx";
        if (format is not ("Pfx" or "Base64"))
        {
            throw new InvalidOperationException("The certificate format must be Pfx or Base64.");
        }
        var path = ProductionSecurityConfigurationValidator.GetCertificatePath(configuration);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A data-protection certificate path is required.");
        }
        var password = ReadValue(configuration, "DataProtection:CertificatePassword",
            Environment.GetEnvironmentVariable("WORKBENCH_DATA_PROTECTION_CERTIFICATE_PASSWORD"));
        var bytes = format == "Base64" ? Convert.FromBase64String(File.ReadAllText(path)) : File.ReadAllBytes(path);
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException("The data-protection certificate must have a private key.");
            }
            return certificate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
