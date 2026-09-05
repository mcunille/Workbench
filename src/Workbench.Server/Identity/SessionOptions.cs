// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Identity;

public sealed class SessionOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromHours(12);

    public TimeSpan LastSeenUpdateInterval { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (IdleTimeout < TimeSpan.FromMinutes(5) || IdleTimeout > TimeSpan.FromHours(8))
        {
            throw new InvalidOperationException("Session idle timeout must be between 5 minutes and 8 hours.");
        }

        if (AbsoluteLifetime < IdleTimeout || AbsoluteLifetime > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("Session absolute lifetime must be between the idle timeout and 24 hours.");
        }

        if (LastSeenUpdateInterval <= TimeSpan.Zero || LastSeenUpdateInterval >= IdleTimeout)
        {
            throw new InvalidOperationException("Session last-seen interval must be positive and shorter than idle timeout.");
        }
    }
}
