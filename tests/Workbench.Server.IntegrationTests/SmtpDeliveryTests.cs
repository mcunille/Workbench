// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Sockets;
using Workbench.Server.Identity;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SmtpDeliveryTests
{
    [Theory]
    [InlineData("", 587, false)]
    [InlineData("smtp.example", 0, false)]
    [InlineData("smtp.example", 65536, false)]
    [InlineData("smtp.example", 1, true)]
    [InlineData("smtp.example", 65535, true)]
    public void SmtpHostAndPortBoundariesAreValidated(string host, int port, bool valid)
    {
        // GIVEN an SMTP endpoint at or outside the supported address boundaries.
        var options = ValidOptions();
        options.Host = host;
        options.Port = port;
        // WHEN configuration is validated, THEN only nonempty hosts and valid TCP ports are accepted.
        if (valid) { options.Validate(); }
        else { Assert.Throws<InvalidOperationException>(options.Validate); }
    }

    [Theory]
    [InlineData(true, false, "recipient@example.com")]
    [InlineData(false, true, "recipient@example.com")]
    [InlineData(false, false, "invalid-recipient")]
    [InlineData(false, false, "recipient@example.com\r\nBcc: injected@example.com")]
    public async Task InvalidMessagesAreRejectedBeforeConnecting(bool expired, bool malformed, string recipient)
    {
        // GIVEN an invalid identity message and a provider whose host must never be contacted.
        var message = new IdentityMessage(IdentityOperationPurpose.PasswordRecovery, recipient,
            malformed ? "invalid" : SessionToken.Create(), DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5));
        // WHEN delivery is attempted, THEN message validation rejects it before SMTP I/O.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SmtpIdentityMessageDelivery(ValidOptions()).DeliverAsync(message, CancellationToken.None));
        Assert.Equal("The identity message is invalid or expired.", error.Message);
    }

    [Theory]
    [InlineData("None", "secret", "https://workbench.example")]
    [InlineData("Auto", "secret", "https://workbench.example")]
    [InlineData("StartTlsWhenAvailable", "secret", "https://workbench.example")]
    [InlineData("StartTls", "", "https://workbench.example")]
    [InlineData("StartTls", "secret", "http://workbench.example")]
    [InlineData("StartTls", "secret", "https://workbench.example/?token=unsafe")]
    public void InsecureSmtpConfigurationFailsClosed(string security, string password, string origin)
    {
        // GIVEN a configured sender with one unsafe deployment setting.
        var options = ValidOptions();
        options.Security = security;
        options.Password = password;
        options.PublicOrigin = origin;
        // WHEN validated, THEN startup rejects the configuration without echoing values.
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=unsafe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedTlsConfigurationIsAccepted()
    {
        // GIVEN mandatory TLS, credentials, and a canonical HTTPS origin.
        var options = ValidOptions();
        // WHEN deployment settings are validated, THEN they are accepted.
        options.Validate();
    }

    [Fact]
    public async Task MissingStartTlsPreventsCredentialTransmission()
    {
        // GIVEN a local SMTP peer which does not offer STARTTLS.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 local test peer");
            var command = await reader.ReadLineAsync(deadline.Token);
            commands.Add(command ?? "");
            await writer.WriteLineAsync("250-local\r\n250 AUTH PLAIN LOGIN");
            try
            {
                commands.Add(await reader.ReadLineAsync(deadline.Token) ?? "");
            }
            catch (IOException) { }
        }, deadline.Token);
        var options = ValidOptions();
        options.Host = "127.0.0.1";
        options.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // WHEN the provider connects, THEN it refuses plaintext authentication.
        await Assert.ThrowsAnyAsync<Exception>(() => new SmtpIdentityMessageDelivery(options).CheckReadyAsync(deadline.Token));
        await peer;
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("EHLO", commands[0], StringComparison.OrdinalIgnoreCase);
    }

    private static SmtpOptions ValidOptions() => new()
    {
        Host = "smtp.example",
        Username = "test-user",
        Password = "secret",
        Sender = "sender@example.com",
        PublicOrigin = "https://workbench.example",
    };

    [Fact]
    public async Task UntrustedTlsCertificatePreventsAuthentication()
    {
        // GIVEN an SMTP peer presenting a self-signed certificate outside the trust store.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receivedApplicationData = false;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new System.Net.Security.SslStream(socket.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                }, deadline.Token);
                var buffer = new byte[1];
                receivedApplicationData = await tls.ReadAsync(buffer, deadline.Token) != 0;
            }
            catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or IOException) { }
        }, deadline.Token);
        var options = ValidOptions();
        options.Host = "localhost";
        options.Security = "SslOnConnect";
        options.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // WHEN TLS validation fails, THEN the provider does not transmit authentication or a message.
        await Assert.ThrowsAsync<MailKit.Security.SslHandshakeException>(() => new SmtpIdentityMessageDelivery(options).CheckReadyAsync(deadline.Token));
        await peer;
        Assert.False(receivedApplicationData);
    }
}
