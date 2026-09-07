// Copyright (c) 2026 The White Stag Collection.

using Azure.Core;
using Azure.Identity;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MimeKit;

namespace Workbench.Server.Identity;

public sealed class GraphOptions
{
    public string MailboxId { get; set; } = "";
    public string ManagedIdentityClientId { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public void Validate()
    {
        if (!Guid.TryParse(MailboxId, out var mailbox) || mailbox == Guid.Empty ||
            !Guid.TryParse(ManagedIdentityClientId, out var identity) || identity == Guid.Empty ||
            !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
        {
            throw new InvalidOperationException("Graph requires mailbox and managed identity IDs and a canonical HTTPS origin.");
        }
    }
}

public sealed class GraphDeliveryException(bool transient, TimeSpan? retryAfter = null)
    : Exception("Graph message submission failed.")
{
    public bool IsTransient { get; } = transient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class GraphIdentityMessageDelivery(GraphOptions options, HttpClient client, TokenCredential credential)
    : IIdentityMessageDelivery
{
    public bool IsAvailable => true;
    public async Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ExpiresAtUtc <= DateTimeOffset.UtcNow || !SessionToken.TryHash(message.Token, out _) ||
            !MailboxAddress.TryParse(message.Recipient, out var recipient) || !recipient.Address.Contains('@') ||
            message.Recipient.Contains('\r') || message.Recipient.Contains('\n'))
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/users/{Guid.Parse(options.MailboxId):D}/sendMail");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = JsonContent.Create(new
            {
                message = new
                {
                    subject = message.Purpose == IdentityOperationPurpose.Invitation ? "Workbench invitation" : "Workbench account recovery",
                    body = new
                    {
                        contentType = "Text",
                        content = $"Use this single-use link before {message.ExpiresAtUtc:O}:\n{link}\nIf you did not request this message, you can ignore it.",
                    },
                    toRecipients = new[] { new { emailAddress = new { address = recipient.Address } } },
                },
                saveToSentItems = false,
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
                {
                    retryAfter = date - DateTimeOffset.UtcNow;
                }
                throw new GraphDeliveryException(transient, retryAfter);
            }
        }
        catch (HttpRequestException) { throw new GraphDeliveryException(true); }
        // Credential failures can wrap temporary managed-identity endpoint outages.
        // Let the durable queue enforce its attempt limit rather than discard valid mail.
        catch (CredentialUnavailableException) { throw new GraphDeliveryException(true); }
        catch (AuthenticationFailedException) { throw new GraphDeliveryException(true); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GraphDeliveryException(true);
        }
    }
}

// Web admission validates configuration but never has a credential capable of sending mail.
public sealed class QueuedGraphIdentityMessageDelivery(GraphOptions options) : IIdentityMessageDelivery
{
    public bool IsAvailable { get { options.Validate(); return true; } }
    public Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Graph delivery is restricted to the worker.");
}

