// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Workbench.Server.Security;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DeploymentSecretsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "workbench-secrets-" + Guid.NewGuid().ToString("N"));

    public DeploymentSecretsTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void MountedValueTakesPrecedenceWithoutTrimmingSignificantWhitespace()
    {
        // GIVEN both a mounted secret and an obsolete inline credential.
        var file = Path.Combine(directory, "connection");
        File.WriteAllText(file, " secret with spaces \r\n");
        var config = Config(("ConnectionStrings:WorkbenchFile", file), ("ConnectionStrings:Workbench", "obsolete"));
        // WHEN loading the credential, THEN only terminal line endings are removed.
        Assert.Equal(" secret with spaces ", DeploymentSecrets.ReadValue(config, "ConnectionStrings:Workbench", "fallback"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("   ")]
    public void EmptyMountedSecretNeverFallsBack(string contents)
    {
        // GIVEN an unusable mounted secret and a usable inline fallback.
        var file = Path.Combine(directory, "empty");
        File.WriteAllText(file, contents);
        var config = Config(("SecretFile", file), ("Secret", "fallback"));
        // WHEN loading it, THEN startup fails closed.
        Assert.Throws<InvalidOperationException>(() => DeploymentSecrets.ReadValue(config, "Secret"));
    }

    [Fact]
    public void MissingMountedSecretNeverFallsBack()
    {
        // GIVEN a missing mounted file and an inline fallback.
        var config = Config(("SecretFile", Path.Combine(directory, "missing")), ("Secret", "fallback"));
        // WHEN loading it, THEN the missing file fails closed.
        Assert.Throws<FileNotFoundException>(() => DeploymentSecrets.ReadValue(config, "Secret"));
    }

    [Fact]
    public void UnmountedValuesPreserveExistingPrecedence()
    {
        // GIVEN no mounted file, WHEN loading configuration, THEN inline configuration wins over legacy fallback.
        Assert.Equal("inline", DeploymentSecrets.ReadValue(Config(("Secret", "inline")), "Secret", "legacy"));
        Assert.Equal("legacy", DeploymentSecrets.ReadValue(Config(), "Secret", "legacy"));
        Assert.Null(DeploymentSecrets.ReadValue(Config(), "Secret"));
    }

    [Theory]
    [InlineData("Pfx")]
    [InlineData("Base64")]
    public void CertificateFormatsLoadTheSamePrivateKey(string format)
    {
        // GIVEN an encrypted PFX mounted directly or as platform Base64 text and a mounted password.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=workbench-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var file = Path.Combine(directory, "certificate");
        var bytes = certificate.Export(X509ContentType.Pfx, "test-password");
        if (format == "Base64") { File.WriteAllText(file, Convert.ToBase64String(bytes)); }
        else { File.WriteAllBytes(file, bytes); }
        var password = Path.Combine(directory, "password");
        File.WriteAllText(password, "test-password\n");
        // WHEN loading the mounted representation, THEN it preserves the certificate and usable private key.
        using var loaded = DeploymentSecrets.LoadCertificate(Config(("DataProtection:CertificatePath", file),
            ("DataProtection:CertificateFormat", format), ("DataProtection:CertificatePasswordFile", password)));
        Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void UnsupportedCertificateFormatIsRejected()
    {
        // GIVEN an unsupported format, WHEN loading a certificate, THEN it is not silently treated as PFX.
        Assert.Throws<InvalidOperationException>(() => DeploymentSecrets.LoadCertificate(Config(
            ("DataProtection:CertificatePath", Path.Combine(directory, "certificate")), ("DataProtection:CertificateFormat", "Pem"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyMountedPathNeverFallsBack(string path)
    {
        // GIVEN an explicitly empty path, WHEN loading a secret, THEN inline values cannot bypass it.
        Assert.Throws<InvalidOperationException>(() => DeploymentSecrets.ReadValue(Config(("SecretFile", path), ("Secret", "fallback")), "Secret"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCertificateMaterialIsRejected(bool wrongPassword)
    {
        // GIVEN either a public-only certificate or an encrypted private key with the wrong password.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=workbench-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var file = Path.Combine(directory, "invalid-certificate");
        File.WriteAllBytes(file, (wrongPassword ? certificate : publicCertificate).Export(X509ContentType.Pfx, "correct"));
        var config = Config(("DataProtection:CertificatePath", file), ("DataProtection:CertificatePassword", wrongPassword ? "wrong" : "correct"));
        // WHEN loading it, THEN invalid decryption authority cannot start the workload.
        if (wrongPassword) { Assert.ThrowsAny<CryptographicException>(() => DeploymentSecrets.LoadCertificate(config)); }
        else { Assert.Throws<InvalidOperationException>(() => DeploymentSecrets.LoadCertificate(config)); }
    }

    [Fact]
    public void RotationRetainsOldKeyDecryptionOnlyWhenPreviousCertificateIsConfigured()
    {
        // GIVEN persisted keys encrypted by the previous certificate and a new release certificate.
        string ExportCertificate(string name)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var path = Path.Combine(directory, name + ".pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "password"));
            return path;
        }
        ServiceProvider Provider(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            var builder = services.AddDataProtection().SetApplicationName("rotation-test")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
            DeploymentSecrets.ConfigureProtection(builder, configuration);
            return services.BuildServiceProvider();
        }
        var previous = ExportCertificate("previous");
        var current = ExportCertificate("current");
        using var original = Provider(Config(("DataProtection:CertificatePath", previous), ("DataProtection:CertificatePassword", "password")));
        var protectedValue = original.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("retained-work");
        // WHEN a release omits the previous certificate, THEN retained encrypted keys cannot be used.
        using var withoutPrevious = Provider(Config(("DataProtection:CertificatePath", current), ("DataProtection:CertificatePassword", "password")));
        Assert.ThrowsAny<CryptographicException>(() => withoutPrevious.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
        // WHEN rotation retains the old certificate, THEN already protected work remains readable.
        using var rotated = Provider(Config(("DataProtection:CertificatePath", current), ("DataProtection:CertificatePassword", "password"),
            ("DataProtection:PreviousCertificates:0:Path", previous), ("DataProtection:PreviousCertificates:0:Password", "password")));
        Assert.Equal("retained-work", rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();

    public void Dispose() => Directory.Delete(directory, true);
}
