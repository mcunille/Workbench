// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BlobIntegrityTests
{
    [Fact]
    public async Task IncrementalReadsEmptyReadsAndRepeatedEofPreserveIntegrity()
    {
        // GIVEN a valid object consumed through small synchronous and asynchronous reads.
        byte[] expected = [1, 2, 3];
        using var underlying = new MemoryStream(expected);
        var stream = BlobIntegrity.Open(underlying, new BlobContentIdentity(3, Convert.ToHexString(SHA256.HashData(expected))));
        // WHEN an empty read precedes ordinary reads, THEN it is not mistaken for EOF.
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        var buffer = new byte[1];
        Assert.Equal(1, stream.Read(buffer, 0, 1));
        Assert.Equal(1, buffer[0]);
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(2, buffer[0]);
        Assert.Equal(1, stream.Read(buffer, 0, 1));
        Assert.Equal(3, buffer[0]);
        // AND repeated EOF reads succeed after the digest is verified exactly once.
        Assert.Equal(0, await stream.ReadAsync(buffer));
        Assert.Equal(0, stream.Read(buffer, 0, 1));
        await stream.DisposeAsync();
        Assert.False(underlying.CanRead);
    }

    [Fact]
    public async Task IntegrityFailureRemainsAFailureOnSubsequentReads()
    {
        // GIVEN a truncated object which has already failed verification.
        await using var stream = BlobIntegrity.Open(new MemoryStream([1]),
            new BlobContentIdentity(3, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }))));
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(Stream.Null));
        // WHEN the caller retries reading EOF, THEN the failed transfer cannot become successful.
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 1, 2, 4 })]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public async Task ReadingToCompletionVerifiesStoredIdentity(byte[] bytes)
    {
        // GIVEN immutable metadata describing three exact bytes.
        byte[] expected = [1, 2, 3];
        await using var stream = BlobIntegrity.Open(new MemoryStream(bytes),
            new BlobContentIdentity(3, Convert.ToHexString(SHA256.HashData(expected))));
        // WHEN a consumer reads the object, THEN only its exact identity reaches successful completion.
        if (bytes.SequenceEqual(expected))
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(expected, output.ToArray());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(Stream.Null));
        }
    }
}
