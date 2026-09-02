// Copyright (c) 2026 The White Stag Collection.

using System.Collections.Concurrent;

namespace Workbench.Server.Identity;

public sealed record IdentityMessage(
    IdentityOperationPurpose Purpose,
    string Recipient,
    string Token,
    DateTimeOffset ExpiresAtUtc);

public interface IIdentityMessageDelivery
{
    bool IsAvailable { get; }

    Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken);
}

public sealed class DevelopmentIdentityMessageDelivery : IIdentityMessageDelivery
{
    private readonly ConcurrentQueue<IdentityMessage> _messages = new();

    public bool IsAvailable => true;

    public IReadOnlyCollection<IdentityMessage> Messages => _messages.ToArray();

    public Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

public sealed class DisabledIdentityMessageDelivery : IIdentityMessageDelivery
{
    public bool IsAvailable => false;

    public Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Identity message delivery is disabled.");
}
