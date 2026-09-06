// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BlobTransferTests
{
    [Fact]
    public async Task ContentIdentityCoversExactlyTheTransferredBytes()
    {
        // GIVEN content crossing the transfer buffer boundary.
        var bytes = RandomNumberGenerator.GetBytes(180_003);
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        // WHEN the bounded stream transfer completes.
        var identity = await BlobTransfer.CopyAsync(source, destination, bytes.Length, CancellationToken.None);
        // THEN stored bytes, length, and SHA-256 agree.
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(bytes.Length, identity.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), identity.Sha256);
    }

    [Fact]
    public async Task ExcessContentIsRejectedBeforeWritingPastTheLimit()
    {
        // GIVEN a stream larger than the allowed content length.
        using var source = new MemoryStream(new byte[200_000]);
        using var destination = new MemoryStream();
        // WHEN the transfer exceeds the limit, THEN it fails without storing excess bytes.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BlobTransfer.CopyAsync(source, destination, 100_000, CancellationToken.None));
        Assert.InRange(destination.Length, 0, 100_000);
    }

    [Fact]
    public async Task CancellationStopsTransfer()
    {
        // GIVEN an already cancelled upload.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream([1, 2, 3]);
        using var destination = new MemoryStream();
        // WHEN transfer is attempted, THEN no content is written.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BlobTransfer.CopyAsync(source, destination, 10, cancellation.Token));
        Assert.Equal(0, destination.Length);
    }
}
