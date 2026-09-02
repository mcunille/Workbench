// Copyright (c) 2026 The White Stag Collection.

using System.Collections.Concurrent;

namespace Workbench.Server.Identity;

public interface ISensitiveRequestRateLimiter
{
    bool IsAvailable { get; }

    ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken);
}

public sealed class DevelopmentSensitiveRequestRateLimiter(TimeProvider timeProvider)
    : ISensitiveRequestRateLimiter
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Window, int Count)> _requests = new();

    public bool IsAvailable => true;

    public ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entry = _requests.AddOrUpdate(
            partition,
            _ => (now, 1),
            (_, current) => now - current.Window >= TimeSpan.FromMinutes(1)
                ? (now, 1)
                : (current.Window, current.Count + 1));
        return ValueTask.FromResult(entry.Count <= 5);
    }
}

public sealed class DisabledSensitiveRequestRateLimiter : ISensitiveRequestRateLimiter
{
    public bool IsAvailable => false;

    public ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}
