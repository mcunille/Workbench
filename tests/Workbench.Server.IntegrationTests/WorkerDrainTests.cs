// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Operations;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class WorkerDrainTests
{
    [Fact]
    public async Task StopsAtItemLimitWithoutAnotherClaim()
    {
        // GIVEN an eligible queue that never empties.
        var calls = 0;
        // WHEN the invocation has a two-item budget.
        var result = await WorkerDrain.RunAsync(_ => { calls++; return Task.FromResult(true); }, 2, TimeSpan.FromSeconds(5));
        // THEN exactly two iterations finish and no third claim starts.
        Assert.Equal(2, calls);
        Assert.Equal(new WorkerDrainResult(2, "ItemLimit"), result);
    }

    [Fact]
    public async Task StopsAtEmptyQueueAndCountsOnlyProcessedClaims()
    {
        // GIVEN two eligible claims followed by an empty queue.
        var calls = 0;
        // WHEN the drain has spare capacity.
        var result = await WorkerDrain.RunAsync(_ => Task.FromResult(++calls <= 2), 10, TimeSpan.FromSeconds(5));
        // THEN the empty probe ends draining without counting as a processed claim.
        Assert.Equal(3, calls);
        Assert.Equal(new WorkerDrainResult(2, "Empty"), result);
    }

    [Fact]
    public async Task PreCancelledInvocationDoesNotClaim()
    {
        // GIVEN the scheduler has already requested shutdown.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        // WHEN a drain starts.
        var result = await WorkerDrain.RunAsync(_ => { calls++; return Task.FromResult(true); }, 10, TimeSpan.FromSeconds(5), cancellation.Token);
        // THEN no work starts and cancellation is reported.
        Assert.Equal(0, calls);
        Assert.Equal(new WorkerDrainResult(0, "Cancelled"), result);
    }

    [Fact]
    public async Task DeadlineCancelsAnInFlightClaim()
    {
        // GIVEN an operation that waits for its cancellation token.
        var calls = 0;
        // WHEN the invocation deadline expires.
        var result = await WorkerDrain.RunAsync(async token =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, 10, TimeSpan.FromMilliseconds(100));
        // THEN the in-flight operation is cancelled and no new claim starts.
        Assert.Equal(1, calls);
        Assert.Equal(new WorkerDrainResult(0, "Deadline"), result);
    }

    [Fact]
    public async Task CancellationBetweenClaimsPreventsTheNextClaim()
    {
        // GIVEN the scheduler cancels after the first completed operation.
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        // WHEN the operation completes.
        var result = await WorkerDrain.RunAsync(_ =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(true);
        }, 10, TimeSpan.FromSeconds(5), cancellation.Token);
        // THEN the completed operation is counted and draining stops.
        Assert.Equal(1, calls);
        Assert.Equal(new WorkerDrainResult(1, "Cancelled"), result);
    }

    [Fact]
    public async Task UnexpectedFailurePropagatesForJobFailureReporting()
    {
        // GIVEN a dependency failure before completing an iteration.
        var failure = new IOException("private-diagnostic");
        // WHEN processing fails.
        var actual = await Assert.ThrowsAsync<IOException>(() => WorkerDrain.RunAsync(_ => throw failure, 10, TimeSpan.FromSeconds(5)));
        // THEN the host can report a failing exit code without pretending the queue was empty.
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task CancellationStopsAnInFlightClaim()
    {
        // GIVEN an operation that observes the scheduler token.
        using var cancellation = new CancellationTokenSource();
        // WHEN the scheduler cancels during the claim.
        var result = await WorkerDrain.RunAsync(async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, 10, TimeSpan.FromSeconds(5), cancellation.Token);
        // THEN the unfinished operation is not counted and cancellation is distinguished from deadline.
        Assert.Equal(new WorkerDrainResult(0, "Cancelled"), result);
    }

    [Fact]
    public async Task UnrelatedCancellationPropagatesAsFailure()
    {
        // GIVEN a dependency cancellation unrelated to the drain budget or scheduler.
        var failure = new OperationCanceledException();
        // WHEN the operation throws before any configured cancellation.
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            WorkerDrain.RunAsync(_ => throw failure, 10, TimeSpan.FromSeconds(5)));
        // THEN the host sees the failure instead of reporting a normal stop.
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task InvalidBudgetsCannotStartWork(int maxItems, int durationMilliseconds)
    {
        // GIVEN an invalid item or duration budget.
        var calls = 0;
        // WHEN the invocation is requested.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WorkerDrain.RunAsync(_ =>
        {
            calls++;
            return Task.FromResult(true);
        }, maxItems, TimeSpan.FromMilliseconds(durationMilliseconds)));
        // THEN invalid configuration fails before any claim.
        Assert.Equal(0, calls);
    }
}
