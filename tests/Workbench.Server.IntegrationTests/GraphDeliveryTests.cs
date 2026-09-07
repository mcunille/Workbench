// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Text.Json;
using Azure.Core;
using Workbench.Server.Identity;
using Workbench.Server.Operations;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GraphDeliveryTests
{
    [Fact]
    public async Task InvitationUsesInvitationPathAndSingleRecipient()
    {
        // GIVEN a valid tenant invitation.
        var token = SessionToken.Create();
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        // WHEN submitted, THEN the invitation path retains the token only in its fragment.
        await new GraphIdentityMessageDelivery(Options(), client, new TestCredential()).DeliverAsync(
            Message(token) with { Purpose = IdentityOperationPurpose.Invitation }, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("Workbench invitation", message.GetProperty("subject").GetString());
        Assert.Contains("https://workbench.example/invite#token=" + token, message.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal(1, message.GetProperty("toRecipients").GetArrayLength());
        Assert.False(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task CancellationDuringTokenAcquisitionPropagatesWithoutSending()
    {
        // GIVEN token acquisition is waiting when the caller cancels.
        using var cancellation = new CancellationTokenSource();
        var credential = new WaitingCredential();
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var task = new GraphIdentityMessageDelivery(Options(), client, credential)
            .DeliverAsync(Message(SessionToken.Create()), cancellation.Token);
        await credential.Started.Task;
        // WHEN cancelled, THEN cancellation propagates and no submission occurs.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TokenServiceFailureUsesBoundedQueueRetriesWithoutExposingDiagnostics()
    {
        // GIVEN a managed identity endpoint outage wrapped by the credential SDK.
        var credential = new TestCredential { Failure = new Azure.Identity.AuthenticationFailedException("sensitive-token-diagnostic", new HttpRequestException("endpoint unavailable")) };
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        // WHEN token acquisition fails, THEN the durable queue can retry and no diagnostics escape.
        var error = await Assert.ThrowsAsync<GraphDeliveryException>(() => new GraphIdentityMessageDelivery(Options(), client, credential)
            .DeliverAsync(Message(SessionToken.Create()), CancellationToken.None));
        Assert.True(error.IsTransient);
        Assert.DoesNotContain("sensitive-token-diagnostic", error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WebProviderCannotSendEvenWithValidConfiguration()
    {
        // GIVEN web admission configured for Graph without a mail credential.
        var delivery = new QueuedGraphIdentityMessageDelivery(Options());
        Assert.True(delivery.IsAvailable);
        // WHEN a direct send is attempted, THEN it is rejected at the web boundary.
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(Message(SessionToken.Create()), CancellationToken.None));
    }

    [Fact]
    public void GraphOriginMustMatchApplicationOrigin()
    {
        // GIVEN otherwise valid configuration pointing identity links at a different host.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Graph:MailboxId"] = Options().MailboxId,
            ["Graph:ManagedIdentityClientId"] = Options().ManagedIdentityClientId,
            ["Graph:PublicOrigin"] = Options().PublicOrigin,
            ["PublicOrigin"] = "https://other.example",
        }).Build();
        // WHEN the runtime reads Graph settings, THEN the mismatch fails closed.
        Assert.Throws<InvalidOperationException>(() => OperationalConfiguration.ReadGraph(configuration));
    }

    [Fact]
    public async Task CancellationDoesNotAcquireCredentials()
    {
        // GIVEN an already cancelled work item.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var credential = new TestCredential();
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        // WHEN submitted, THEN cancellation propagates before any I/O.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GraphIdentityMessageDelivery(Options(), client, credential)
            .DeliverAsync(Message(SessionToken.Create()), cancellation.Token));
        Assert.Null(credential.Scopes);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ThrottlingPreservesRetryAfterForTheDurableQueue()
    {
        // GIVEN Graph throttles submission for two minutes.
        var handler = new Handler(HttpStatusCode.TooManyRequests) { RetryAfter = TimeSpan.FromMinutes(2) };
        using var client = new HttpClient(handler);
        // WHEN submitted, THEN the queue receives the delay without an inline retry.
        var error = await Assert.ThrowsAsync<GraphDeliveryException>(() => new GraphIdentityMessageDelivery(Options(), client, new TestCredential())
            .DeliverAsync(Message(SessionToken.Create()), CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(2), error.RetryAfter);
        Assert.True(error.IsTransient);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("invalid", "https://workbench.example")]
    [InlineData("00000000-0000-0000-0000-000000000000", "https://workbench.example")]
    [InlineData("5c9a104a-55b5-47cd-9db3-e674ad4b02ec", "http://workbench.example")]
    [InlineData("5c9a104a-55b5-47cd-9db3-e674ad4b02ec", "https://user:password@workbench.example")]
    public void InvalidMailboxOrOriginIsRejected(string mailbox, string origin)
    {
        // GIVEN an invalid configured mailbox or origin.
        var options = Options();
        options.MailboxId = mailbox;
        options.PublicOrigin = origin;
        // WHEN validated, THEN no unsafe configuration is accepted.
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
    [Fact]
    public async Task AcceptedSubmissionUsesOnlyConfiguredMailboxAndFragmentLink()
    {
        // GIVEN a valid recovery message and a Graph endpoint accepting submission.
        var token = SessionToken.Create();
        var credential = new TestCredential();
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var delivery = new GraphIdentityMessageDelivery(Options(), client, credential);
        // WHEN the worker submits the message.
        await delivery.DeliverAsync(Message(token), CancellationToken.None);
        // THEN the configured mailbox is the only sender and the token stays in the link fragment.
        Assert.Equal("https://graph.microsoft.com/v1.0/users/5c9a104a-55b5-47cd-9db3-e674ad4b02ec/sendMail", handler.Url);
        Assert.Equal("https://graph.microsoft.com/.default", Assert.Single(credential.Scopes!));
        using var json = JsonDocument.Parse(handler.Body!);
        var message = json.RootElement.GetProperty("message");
        Assert.Contains("https://workbench.example/recover#token=" + token, message.GetProperty("body").GetProperty("content").GetString());
        Assert.False(message.TryGetProperty("from", out _));
        Assert.Equal("recipient@example.com", message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(302, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task FailedResponsesAreSafeAndDoNotRetryInline(int status, bool transient)
    {
        // GIVEN a provider rejection containing sensitive diagnostic text.
        var handler = new Handler((HttpStatusCode)status);
        using var client = new HttpClient(handler);
        // WHEN submission fails, THEN only safe classification leaves the provider.
        var error = await Assert.ThrowsAsync<GraphDeliveryException>(() =>
            new GraphIdentityMessageDelivery(Options(), client, new TestCredential()).DeliverAsync(Message(SessionToken.Create()), CancellationToken.None));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-response", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExpiredMessageDoesNotAcquireTokenOrSend()
    {
        // GIVEN an expired message.
        var credential = new TestCredential();
        var handler = new Handler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        // WHEN submitted, THEN validation rejects it before credential or network access.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GraphIdentityMessageDelivery(Options(), client, credential)
            .DeliverAsync(Message(SessionToken.Create()) with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None));
        Assert.Null(credential.Scopes);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void InvalidConfigurationFailsClosed()
    {
        // GIVEN a Graph origin containing an injected token query.
        var options = Options();
        options.PublicOrigin = "https://workbench.example/?token=secret";
        // WHEN validated, THEN the invalid configuration is rejected without echoing it.
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static GraphOptions Options() => new()
    {
        MailboxId = "5c9a104a-55b5-47cd-9db3-e674ad4b02ec",
        ManagedIdentityClientId = "6fd06711-feb9-410b-8195-4d1ed4d4a174",
        PublicOrigin = "https://workbench.example"
    };
    private static IdentityMessage Message(string token) => new(IdentityOperationPurpose.PasswordRecovery,
        "recipient@example.com", token, DateTimeOffset.UtcNow.AddMinutes(5));
    private sealed class TestCredential : TokenCredential
    {
        public Exception? Failure { get; init; }
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes = context.Scopes;
            if (Failure is not null) { throw Failure; }
            return ValueTask.FromResult(new AccessToken("test-access-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }
    private sealed class WaitingCredential : TokenCredential
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected.");
        }
    }
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public TimeSpan? RetryAfter { get; init; }
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent("sensitive-response") };
            if (RetryAfter is { } delay) { response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay); }
            return response;
        }
    }
}
