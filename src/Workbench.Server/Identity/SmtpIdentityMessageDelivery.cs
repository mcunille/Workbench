// Copyright (c) 2026 The White Stag Collection.

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Workbench.Server.Identity;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Sender { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 ||
            Security is not ("StartTls" or "SslOnConnect") ||
            string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password) ||
            !MailboxAddress.TryParse(Sender, out var sender) || !sender.Address.Contains('@') || Sender.Contains('\r') || Sender.Contains('\n') ||
            !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
        {
            throw new InvalidOperationException("SMTP requires authenticated TLS, a valid sender, and a canonical HTTPS origin.");
        }
    }
}

public sealed class SmtpIdentityMessageDelivery(SmtpOptions options) : IIdentityMessageDelivery
{
    public bool IsAvailable => true;
    public async Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken)
    {
        options.Validate();
        if (message.ExpiresAtUtc <= DateTimeOffset.UtcNow || !SessionToken.TryHash(message.Token, out _) ||
            !MailboxAddress.TryParse(message.Recipient, out var recipient) || !recipient.Address.Contains('@') || message.Recipient.Contains('\r') || message.Recipient.Contains('\n'))
        {
            throw new InvalidOperationException("The identity message is invalid or expired.");
        }
        var path = message.Purpose switch
        {
            IdentityOperationPurpose.PasswordRecovery => "/recover",
            IdentityOperationPurpose.Invitation => "/invite",
            _ => throw new InvalidOperationException("Unsupported identity message purpose."),
        };
        var link = new Uri(new Uri(options.PublicOrigin), path).AbsoluteUri + "#token=" + Uri.EscapeDataString(message.Token);
        using var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(options.Sender));
        mail.To.Add(recipient);
        mail.Subject = message.Purpose == IdentityOperationPurpose.Invitation ? "Workbench invitation" : "Workbench account recovery";
        mail.Body = new TextPart("plain") { Text = $"Use this single-use link before {message.ExpiresAtUtc:O}:\n{link}\nIf you did not request this message, you can ignore it." };
        using var client = new SmtpClient { Timeout = 15000 };
        await ConnectAsync(client, cancellationToken);
        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        using var client = new SmtpClient { Timeout = 15000 };
        await ConnectAsync(client, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task ConnectAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        // Do not attach a protocol logger or override certificate validation.
        await client.ConnectAsync(options.Host, options.Port,
            options.Security == "StartTls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
            cancellationToken);
        await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
    }
}
