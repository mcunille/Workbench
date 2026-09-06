// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Operations;

public sealed record WorkerDrainResult(int ProcessedCount, string StopReason);

public static class WorkerDrain
{
    public static async Task<WorkerDrainResult> RunAsync(Func<CancellationToken, Task<bool>> process,
        int maxItems, TimeSpan maxDuration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        if (maxDuration <= TimeSpan.Zero || maxDuration.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDuration));
        }
        using var deadline = new CancellationTokenSource(maxDuration);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var processed = 0;
        while (processed < maxItems)
        {
            if (stopping.IsCancellationRequested)
            {
                return new(processed, cancellationToken.IsCancellationRequested ? "Cancelled" : "Deadline");
            }
            try
            {
                if (!await process(stopping.Token))
                {
                    return new(processed, "Empty");
                }
                processed++;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return new(processed, cancellationToken.IsCancellationRequested ? "Cancelled" : "Deadline");
            }
        }
        return new(processed, "ItemLimit");
    }
}
